namespace Certyfi.Domain.Entities;

// An image the institution shows beside the student portal (login page and
// the student's certificates): its own courses, events, admissions. Only
// the institution's own images and links; no third-party ad code runs on
// pages with students' personal data. Managed by Institution Admins.
public class PortalBanner
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = default!;
    public string ImageUrl { get; set; } = default!;
    // Optional; opened in a new tab.
    public string? LinkUrl { get; set; }
    public PortalBannerSide Side { get; set; }
    public int SortOrder { get; set; }
    // Optional run dates (inclusive), in the institution's calendar.
    public DateOnly? StartsOn { get; set; }
    public DateOnly? EndsOn { get; set; }
    public bool IsActive { get; set; } = true;
    public int ClickCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum PortalBannerSide
{
    Left = 0,
    Right = 1,
}
