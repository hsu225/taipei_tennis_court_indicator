namespace CourtFinder.Core.Models;

public class Availability
{
    public string CourtId { get; set; } = string.Empty;
    public DateOnly Date { get; set; }
    public List<TimeSlot> Slots { get; set; } = new();
}

