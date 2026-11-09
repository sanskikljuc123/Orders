// One Aspire app host, one browser and one fake payment service (with a global mode) are shared
// by the whole run, so scenarios run sequentially. Scenarios still use their own browser
// context and their own data, so this can be relaxed once the payment fake's mode is per-request.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
