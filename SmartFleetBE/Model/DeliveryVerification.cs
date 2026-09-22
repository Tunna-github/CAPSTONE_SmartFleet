using System;
using System.Collections.Generic;

namespace SmartFleetBE.Model;

public partial class DeliveryVerification
{
    public long VerificationId { get; set; }

    public long MissionId { get; set; }

    public string VerificationStage { get; set; } = null!;

    public string VerificationType { get; set; } = null!;

    public string? ExpectedQrPayload { get; set; }

    public string? QrPayloadScanned { get; set; }

    public bool IsVerified { get; set; }

    public DateTime? VerifiedAt { get; set; }

    public int? VerifiedBy { get; set; }

    public string? Note { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual Mission Mission { get; set; } = null!;

    public virtual User? VerifiedByNavigation { get; set; }
}
