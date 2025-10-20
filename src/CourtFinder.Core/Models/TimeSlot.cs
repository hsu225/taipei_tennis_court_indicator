namespace CourtFinder.Core.Models;

public class TimeSlot
{
    public TimeOnly Start { get; set; }
    public TimeOnly End { get; set; }
    public bool IsAvailable { get; set; }
    public string? SourceNote { get; set; }

    public override string ToString()
        => $"{Start.ToString("HH:mm")}-{End.ToString("HH:mm")} {(IsAvailable ? "Available" : "Booked")}";
}

