using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime.Scheduler;

namespace Orleans.Runtime.GrainDirectory
{
    /// <summary>
    /// Most methods of this class are synchronized since they might be called both
    /// from LocalGrainDirectory on CacheValidator.SchedulingContext and from RemoteGrainDirectory.
    /// </summary>
    internal class GrainDirectoryHandoffManager
    {
        private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);
        private const int MAX_OPERATION_DEQUEUE = 2;
        private readonly LocalGrainDirectory localDirectory;
        private readonly ISiloStatusOracle siloStatusOracle;
        private readonly IInternalGrainFactory grainFactory;
        private readonly Dictionary<SiloAddress, GrainDirectoryPartition> directoryPartitionsMap;
        private readonly List<SiloAddress> silosHoldingMyPartition;
        private readonly Dictionary<SiloAddress, Task> lastPromise;
        private readonly ILogger logger;
        private readonly Factory<GrainDirectoryPartition> createPartion;
        private readonly Queue<(string name, Func<Task> action)> pendingOperations = new Queue<(string name, Func<Task> action)>();
        private readonly AsyncLock executorLock = new AsyncLock();

        internal GrainDirectoryHandoffManager(
            LocalGrainDirectory localDirectory,
            ISiloStatusOracle siloStatusOracle,
            IInternalGrainFactory grainFactory,
            Factory<GrainDirectoryPartition> createPartion,
            ILoggerFactory loggerFactory)
        {
            logger = loggerFactory.CreateLogger<GrainDirectoryHandoffManager>();
            this.localDirectory = localDirectory;
            this.siloStatusOracle = siloStatusOracle;
            this.grainFactory = grainFactory;
            this.createPartion = createPartion;
            directoryPartitionsMap = new Dictionary<SiloAddress, GrainDirectoryPartition>();
            silosHoldingMyPartition = new List<SiloAddress>();
            lastPromise = new Dictionary<SiloAddress, Task>();
        }

        internal void ProcessSiloRemoveEvent(SiloAddress removedSilo)
        {
            lock (this)
            {
                if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("Processing silo remove event for {RemovedSilo}", removedSilo);

                // Reset our follower list to take the changes into account
                ResetFollowers();

                // check if this is one of our successors (i.e., if I hold this silo's copy)
                // (if yes, adjust local and/or handoffed directory partitions)
                if (!directoryPartitionsMap.TryGetValue(removedSilo, out var partition)) return;

                // at least one predcessor should exist, which is me
                SiloAddress predecessor = localDirectory.FindPredecessors(removedSilo, 1)[0];
                Debug.Assert(predecessor is not null);
                Dictionary<SiloAddress, List<GrainAddress>> duplicates;
                if (localDirectory.MyAddress.Equals(predecessor))
                {
                    if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("Merging my partition with the copy of silo {RemovedSilo}", removedSilo);
                    // now I am responsible for this directory part
                    duplicates = localDirectory.DirectoryPartition.Merge(partition);
                    // no need to send our new partition to all others, as they
                    // will realize the change and combine their copies without any additional communication (see below)
                }
                else
                {
                    if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("Merging partition of {Predecessor} with the copy of silo {RemovedSilo}", predecessor, removedSilo);
                    // adjust copy for the predecessor of the failed silo
                    duplicates = directoryPartitionsMap[predecessor].Merge(partition);
                }

                if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("Removed copied partition of silo {RemovedSilo}", removedSilo);
                directoryPartitionsMap.Remove(removedSilo);
                DestroyDuplicateActivations(duplicates);
            }
        }

        internal void ProcessSiloAddEvent(SiloAddress addedSilo)
        {
            lock (this)
            {
                if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("Processing silo add event for {AddedSilo}", addedSilo);

                // Reset our follower list to take the changes into account
                ResetFollowers();

                // check if this is one of our successors (i.e., if I should hold this silo's copy)
                // (if yes, adjust local and/or copied directory partitions by splitting them between old successors and the new one)
                // NOTE: We need to move part of our local directory to the new silo if it is an immediate successor.
                List<SiloAddress> successors = localDirectory.FindSuccessors(localDirectory.MyAddress, 1);
                if (!successors.Contains(addedSilo))
                {
                    if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("{AddedSilo} is not one of my successors.", addedSilo);
                    return;
                }

                // check if this is an immediate successor
                if (successors[0].Equals(addedSilo))
                {
                    // split my local directory and send to my new immediate successor his share
                    if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("Splitting my partition between me and {AddedSilo}", addedSilo);
                    GrainDirectoryPartition splitPart = localDirectory.DirectoryPartition.Split(
                        grain =>
                        {
                            var s = localDirectory.CalculateGrainDirectoryPartition(grain);
                            return (s != null) && !localDirectory.MyAddress.Equals(s);
                        }, false);
                    List<GrainAddress> splitPartListSingle = splitPart.ToListOfActivations();

                    EnqueueOperation(
                        $"{nameof(ProcessSiloAddEvent)}({addedSilo})",
                        () => ProcessAddedSiloAsync(addedSilo, splitPartListSingle));
                }
                else
                {
                    // adjust partitions by splitting them accordingly between new and old silos
                    SiloAddress predecessorOfNewSilo = localDirectory.FindPredecessors(addedSilo, 1)[0];
                    if (!directoryPartitionsMap.TryGetValue(predecessorOfNewSilo, out var predecessorPartition))
                    {
                        // we should have the partition of the predcessor of our new successor
                        logger.LogWarning((int)ErrorCode.DirectoryPartitionPredecessorExpected, "This silo is expected to hold directory partition of {PredecessorOfNewSilo}", predecessorOfNewSilo);
                    }
                    else
                    {
                        if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("Splitting partition of {Predecessor} and creating a copy for {AddedSilo}", predecessorOfNewSilo, addedSilo);
                        GrainDirectoryPartition splitPart = predecessorPartition.Split(
                            grain =>
                            {
                                // Need to review the 2nd line condition.
                                var s = localDirectory.CalculateGrainDirectoryPartition(grain);
                                return (s != null) && !predecessorOfNewSilo.Equals(s);
                            }, true);
                        directoryPartitionsMap[addedSilo] = splitPart;
                    }
                }

                // remove partition of one of the old successors that we do not need to now
                SiloAddress oldSuccessor = directoryPartitionsMap.FirstOrDefault(pair => !successors.Contains(pair.Key)).Key;
                if (oldSuccessor == null) return;

                if (logger.IsEnabled(LogLevel.Debug))
                {
                    logger.LogDebug(
                        "Removing copy of the directory partition of silo {OldSuccessor} (holding copy of {AddedSilo} instead)",
                        oldSuccessor,
                        addedSilo);
                }

                directoryPartitionsMap.Remove(oldSuccessor);
            }
        }

        private async Task ProcessAddedSiloAsync(SiloAddress addedSilo, List<GrainAddress> splitPartListSingle)
        {
            if (!this.localDirectory.Running) return;

            if (this.siloStatusOracle.GetApproximateSiloStatus(addedSilo) == SiloStatus.Active)
            {
                if (splitPartListSingle.Count > 0)
                {
                    if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("Sending {Count} single activation entries to {AddedSilo}", splitPartListSingle.Count, addedSilo);
                }

                await localDirectory.GetDirectoryReference(addedSilo).AcceptSplitPartition(splitPartListSingle);
            }
            else
            {
                if (logger.IsEnabled(LogLevel.Warning)) logger.LogWarning("Silo " + addedSilo + " is no longer active and therefore cannot receive this partition split");
                return;
            }

            if (splitPartListSingle.Count > 0)
            {
                if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("Removing {Count} single activation after partition split", splitPartListSingle.Count);

                splitPartListSingle.ForEach(
                    activationAddress =>
                        localDirectory.DirectoryPartition.RemoveGrain(activationAddress.GrainId));
            }
        }

        internal void AcceptExistingRegistrations(List<GrainAddress> singleActivations)
        {
            this.EnqueueOperation(
                nameof(AcceptExistingRegistrations),
                () => AcceptExistingRegistrationsAsync(singleActivations));
        }

        private async Task AcceptExistingRegistrationsAsync(List<GrainAddress> singleActivations)
        {
            if (!this.localDirectory.Running) return;

            if (this.logger.IsEnabled(LogLevel.Debug))
            {
                this.logger.LogDebug($"{nameof(AcceptExistingRegistrations)}: accepting {{Count}} single-activation registrations", singleActivations?.Count ?? 0);
            }

            if (singleActivations != null && singleActivations.Count > 0)
            {
                var tasks = singleActivations.Select(addr => this.localDirectory.RegisterAsync(addr, 1)).ToArray();
                try
                {
                    await Task.WhenAll(tasks);
                }
                catch (Exception exception)
                {
                    if (this.logger.IsEnabled(LogLevel.Warning))
                        this.logger.LogWarning(exception, $"Exception registering activations in {nameof(AcceptExistingRegistrations)}");
                    throw;
                }
                finally
                {
                    Dictionary<SiloAddress, List<GrainAddress>> duplicates = new Dictionary<SiloAddress, List<GrainAddress>>();
                    for (var i = tasks.Length - 1; i >= 0; i--)
                    {
                        // Retry failed tasks next time.
                        if (tasks[i].Status != TaskStatus.RanToCompletion) continue;

                        // Record the applications which lost the registration race (duplicate activations).
                        var winner = await tasks[i];
                        if (!winner.Address.Equals(singleActivations[i]))
                        {
                            var duplicate = singleActivations[i];

                            if (!duplicates.TryGetValue(duplicate.SiloAddress, out var activations))
                            {
                                activations = duplicates[duplicate.SiloAddress] = new List<GrainAddress>(1);
                            }

                            activations.Add(duplicate);
                        }

                        // Remove tasks which completed.
                        singleActivations.RemoveAt(i);
                    }

                    // Destroy any duplicate activations.
                    DestroyDuplicateActivations(duplicates);
                }
            }
        }

        internal void AcceptHandoffPartition(SiloAddress source, Dictionary<GrainId, GrainInfo> partition, bool isFullCopy)
        {
            lock (this)
            {
                if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("Got request to register {CopyType}, directory partition with {Count} elements from {Source}", isFullCopy ? "FULL" : "DELTA", partition.Count, source);

                if (!directoryPartitionsMap.TryGetValue(source, out var sourcePartition))
                {
                    if (!isFullCopy)
                    {
                        logger.LogWarning(
                            (int)ErrorCode.DirectoryUnexpectedDelta,
                            "Got delta of the directory partition from silo {SiloAddress} (Membership status {Status}) while not holding a full copy. Membership active cluster size is {ClusterSize}",
                            source,
                            this.siloStatusOracle.GetApproximateSiloStatus(source),
                            this.siloStatusOracle.GetApproximateSiloStatuses(true).Count);
                    }

                    directoryPartitionsMap[source] = sourcePartition = this.createPartion();
                }

                if (isFullCopy)
                {
                    sourcePartition.Set(partition);
                }
                else
                {
                    sourcePartition.Update(partition);
                }
            }
        }

        internal void RemoveHandoffPartition(SiloAddress source)
        {
            lock (this)
            {
                if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("Got request to unregister directory partition copy from {Source}", source);
                directoryPartitionsMap.Remove(source);
            }
        }

        private void ResetFollowers()
        {
            var copyList = silosHoldingMyPartition.ToList();
            foreach (var follower in copyList)
            {
                RemoveOldFollower(follower);
            }
        }

        private void RemoveOldFollower(SiloAddress silo)
        {
            if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("Removing my copy from silo {Silo}", silo);
            // release this old copy, as we have got a new one
            silosHoldingMyPartition.Remove(silo);
            localDirectory.RemoteGrainDirectory.WorkItemGroup.QueueTask(
                    () => localDirectory.GetDirectoryReference(silo).RemoveHandoffPartition(localDirectory.MyAddress),
                    localDirectory.RemoteGrainDirectory)
                .Ignore();
        }

        private void DestroyDuplicateActivations(Dictionary<SiloAddress, List<GrainAddress>> duplicates)
        {
            if (duplicates == null || duplicates.Count == 0) return;
            this.EnqueueOperation(
                nameof(DestroyDuplicateActivations),
                () => DestroyDuplicateActivationsAsync(duplicates));
        }

        private async Task DestroyDuplicateActivationsAsync(Dictionary<SiloAddress, List<GrainAddress>> duplicates)
        {
            while (duplicates.Count > 0)
            {
                var pair = duplicates.FirstOrDefault();
                if (this.siloStatusOracle.GetApproximateSiloStatus(pair.Key) == SiloStatus.Active)
                {
                    if (this.logger.IsEnabled(LogLevel.Debug))
                    {
                        this.logger.LogDebug(
                            $"{nameof(DestroyDuplicateActivations)} will destroy {{Count}} duplicate activations on silo {{SiloAddress}}: {{Duplicates}}",
                            duplicates.Count,
                            pair.Key,
                            string.Join("\n * ", pair.Value.Select(_ => _)));
                    }

                    var remoteCatalog = this.grainFactory.GetSystemTarget<ICatalog>(Constants.CatalogType, pair.Key);
                    await remoteCatalog.DeleteActivations(pair.Value, DeactivationReasonCode.DuplicateActivation, "This grain has been activated elsewhere");
                }

                duplicates.Remove(pair.Key);
            }
        }

        private void EnqueueOperation(string name, Func<Task> action)
        {
            lock (this)
            {
                this.pendingOperations.Enqueue((name, action));
                if (this.pendingOperations.Count <= 2)
                {
                    this.localDirectory.RemoteGrainDirectory.WorkItemGroup.QueueTask(ExecutePendingOperations, localDirectory.RemoteGrainDirectory);
                }
            }
        }

        private async Task ExecutePendingOperations()
        {
            using (await executorLock.LockAsync())
            {
                var dequeueCount = 0;
                while (true)
                {
                    // Get the next operation, or exit if there are none.
                    (string Name, Func<Task> Action) op;
                    lock (this)
                    {
                        if (this.pendingOperations.Count == 0) break;

                        op = this.pendingOperations.Peek();
                    }

                    dequeueCount++;

                    try
                    {
                        await op.Action();
                        // Success, reset the dequeue count
                        dequeueCount = 0;
                    }
                    catch (Exception exception)
                    {
                        if (dequeueCount < MAX_OPERATION_DEQUEUE)
                        {
                            if (this.logger.IsEnabled(LogLevel.Warning))
                                this.logger.LogWarning(exception, "{Operation} failed, will be retried", op.Name);
                            await Task.Delay(RetryDelay);
                        }
                        else
                        {
                            if (this.logger.IsEnabled(LogLevel.Warning))
                                this.logger.LogWarning(exception, "{Operation} failed, will NOT be retried", op.Name);
                        }
                    }
                    if (dequeueCount == 0 || dequeueCount >= MAX_OPERATION_DEQUEUE)
                    {
                        lock (this)
                        {
                            // Remove the operation from the queue if it was a success
                            // or if we tried too many times
                            this.pendingOperations.Dequeue();
                        }
                    }
                }
            }
        }
    }
}
