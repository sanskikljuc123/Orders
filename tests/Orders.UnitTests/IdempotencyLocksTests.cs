/// <summary>The per-key lock behind "charge once": one holder at a time, nothing left behind.</summary>
public class IdempotencyLocksTests
{
    private readonly IdempotencyLocks _locks = new();

    [Fact]
    public async Task Held_lock_is_counted_and_released_lock_is_removed()
    {
        var held = await _locks.AcquireAsync("k");

        Assert.Equal(1, _locks.Count);
        Assert.Equal(1, _locks.UsersOf("k"));

        held.Dispose();

        Assert.Equal(0, _locks.Count);
        Assert.Equal(0, _locks.UsersOf("k"));
    }

    [Fact]
    public async Task Second_caller_for_the_same_key_waits_until_the_first_releases()
    {
        var first = await _locks.AcquireAsync("k");

        var second = _locks.AcquireAsync("k");

        Assert.False(second.IsCompleted);
        Assert.Equal(2, _locks.UsersOf("k"));
        first.Dispose();
        (await second.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
        Assert.Equal(0, _locks.Count);
    }

    [Fact]
    public async Task Different_keys_do_not_wait_for_each_other()
    {
        using var a = await _locks.AcquireAsync("a");

        var b = _locks.AcquireAsync("b");

        Assert.True(b.IsCompletedSuccessfully);
        (await b).Dispose();
    }
}
