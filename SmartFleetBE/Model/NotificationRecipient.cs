using System;
using System.Collections.Generic;

namespace SmartFleetBE.Model;

public partial class NotificationRecipient
{
    public long NotificationId { get; set; }

    public int UserId { get; set; }

    public bool IsRead { get; set; }

    public DateTime? ReadAt { get; set; }

    public virtual Notification Notification { get; set; } = null!;

    public virtual User User { get; set; } = null!;
}
