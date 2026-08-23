using System.Collections.Generic;

namespace AlmanacIlluminated;

/// <summary>How well the reader knows one crop. Three tiers, and nothing between them.</summary>
public enum CropTier
{
    /// <summary>Never grown, or not grown enough for the plant to have told you anything.</summary>
    Stranger,
    /// <summary>The shape of its needs, in words.</summary>
    Acquainted,
    /// <summary>Its exact figures.</summary>
    Versed,
}

/// <summary>
/// One crop's standing with the reader, as the supplying mod reckons it. Counts are
/// reported alongside the tier because the book is where a person takes stock, and the
/// tab may choose to show the tally; the item-level surfaces deliberately do not.
/// </summary>
public sealed class CropStanding
{
    /// <summary>The supplier's own id for this crop, used only for stable per-crop jitter.</summary>
    public string CropId = "";

    /// <summary>Family this crop groups under, matching a <see cref="CropFamilyStanding.Id"/>.</summary>
    public string FamilyId = "";

    public CropTier Tier;

    /// <summary>Harvests of this crop alone.</summary>
    public int OwnCount;

    /// <summary>Own harvests plus whatever share of its kin's the supplier counts.</summary>
    public double EffectiveCount;

    /// <summary>Effective count at which Acquainted and Versed open. Server-tunable, so
    /// the tab reads them rather than assuming the shipped 5 and 25.</summary>
    public int AcquaintedAt = 5;
    public int VersedAt = 25;

    /// <summary>Never grown, but the kin have carried something across.</summary>
    public bool KinOnly => OwnCount <= 0 && EffectiveCount > 0;
}

/// <summary>A plant family as the taxonomy owner defines it, with the reader's standing in it.</summary>
public sealed class CropFamilyStanding
{
    public string Id = "";

    /// <summary>Printed as the section head.</summary>
    public string Name = "";

    /// <summary>A quirk of this family's grouping worth admitting on the page (potato filed
    /// with the roots, the two leeks sharing one pool). Null where there is nothing to explain.</summary>
    public string? Note;

    /// <summary>Position in the taxonomy's own order. The book does not re-derive botany.</summary>
    public int Order;

    public int MemberCount;

    /// <summary>Summed harvests across the whole family.</summary>
    public int Sum;

    /// <summary>The family's shared threshold has been reached.</summary>
    public bool Versed;

    /// <summary>The family-wide reading is actually open to this reader — the threshold met
    /// AND whatever else the supplier gates it on. Kept apart from <see cref="Versed"/> so
    /// the page never promises a sense the reader has not got.</summary>
    public bool MemoryAvailable;

    /// <summary>Nothing else in this family, so nothing grown elsewhere can warm it.</summary>
    public bool OfOne => MemberCount <= 1;
}

/// <summary>
/// Supplies per-crop familiarity to the Crops tab. Register through
/// AlmanacIlluminatedModSystem.RegisterCropFamiliarity (client side, before the book
/// first opens). Illuminated never requires the supplier: with none registered, or with
/// one whose <see cref="Enabled"/> is false, the tab renders its plain catalogue exactly
/// as it always has.
///
/// The supplier owns the taxonomy, the thresholds and the wording of its own quirks.
/// The book owns how any of it is drawn.
/// </summary>
public interface ICropFamiliaritySource
{
    /// <summary>False when the server has turned the whole system off. Checked on every
    /// build, so a mid-session config change lands on the next open.</summary>
    bool Enabled { get; }

    /// <summary>The share of a kin's harvest that carries across, 0 to 1. The page says out
    /// loud how far knowledge travels, so it has to be told rather than assume the shipped half.</summary>
    double Spread { get; }

    /// <summary>This entry's standing, or null when the entry is not a crop this supplier
    /// tracks. An untracked crop keeps the plain card: the book states what it knows and
    /// does not pretend an unlisted plant is a stranger.</summary>
    CropStanding? Of(CropEntry entry);

    /// <summary>Every family, in the taxonomy's own order.</summary>
    IReadOnlyList<CropFamilyStanding> Families { get; }
}
