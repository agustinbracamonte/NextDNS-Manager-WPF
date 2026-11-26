namespace NextDNSModernWPF;

/// <summary>Small, UI-independent decisions for loading Activity when its tab opens.</summary>
public static class NextDnsActivityAutoLoadPolicy
{
    public static bool ShouldQueueForCurrentVisit(
        bool activityVisible,
        bool apiConnected,
        bool profileSelected,
        bool filtersNeedApply,
        bool requestAlreadyIssuedForVisit,
        bool activityRequestInProgress) =>
        activityVisible && apiConnected && profileSelected && !filtersNeedApply &&
        !requestAlreadyIssuedForVisit && !activityRequestInProgress;

    public static bool ShouldStartQueuedLoad(
        bool activityVisible,
        bool apiConnected,
        bool profileSelected,
        bool filtersNeedApply,
        bool autoLoadPending,
        bool activityRequestInProgress,
        bool anotherApiOperationInProgress) =>
        activityVisible && apiConnected && profileSelected && !filtersNeedApply && autoLoadPending &&
        !activityRequestInProgress && !anotherApiOperationInProgress;
}
