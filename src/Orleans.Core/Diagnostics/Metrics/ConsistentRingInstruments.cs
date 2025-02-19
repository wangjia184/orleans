using System;
using System.Diagnostics.Metrics;

namespace Orleans.Runtime;

internal static class ConsistentRingInstruments
{
    internal static ObservableGauge<int> RingSize;
    internal static void RegisterRingSizeObserve(Func<int> observeValue)
    {
        RingSize = Instruments.Meter.CreateObservableGauge(InstrumentNames.CONSISTENTRING_RINGSIZE, observeValue);
    }

    internal static ObservableGauge<float> MyRangeRingPercentage;
    internal static void RegisterMyRangeRingPercentageObserve(Func<float> observeValue)
    {
        MyRangeRingPercentage = Instruments.Meter.CreateObservableGauge(InstrumentNames.CONSISTENTRING_MYRANGE_RINGPERCENTAGE, observeValue);
    }
    internal static ObservableGauge<float> AverageRingPercentage;
    internal static void RegisterAverageRingPercentageObserve(Func<float> observeValue)
    {
        AverageRingPercentage = Instruments.Meter.CreateObservableGauge(InstrumentNames.CONSISTENTRING_AVERAGERINGPERCENTAGE, observeValue);
    }
}
