using Oom.Contracts;
using Oom.Tests.Scars.Fixtures;

namespace Oom.Tests.Scars;

public sealed class TestDisipliniScars
{

    [Fact(DisplayName = "Y-078 · Süit gece yarısının iki yanında aynı sonucu verir")]
    public void Y078_DateBoundaryUsesFakeNow()
    {
        var before = new DateTimeOffset(2026, 9, 8, 23, 59, 0, TimeSpan.FromHours(3));
        var after = before.AddMinutes(2);
        var doctor = new Doctor();
        var first = doctor.Check(before);
        var second = doctor.Check(after);
        Assert.Equal(first.Coverage, second.Coverage);
        Assert.Equal(first.RejectionRate, second.RejectionRate);
    }

}
