using System;

DateTimeOffset Normalize(ScheduledFuture future) => future switch
{
    TimeSpan delay => DateTimeOffset.UtcNow + delay,
    DateTimeOffset instant => instant,
    // Unspecified DateTime values use the local time zone.
    DateTime instant => new DateTimeOffset(instant),
};

Console.WriteLine(Normalize(TimeSpan.FromMinutes(5)));
Console.WriteLine(Normalize(DateTimeOffset.UtcNow));
Console.WriteLine(Normalize(DateTime.UtcNow));

union ScheduledFuture(TimeSpan, DateTimeOffset, DateTime);
