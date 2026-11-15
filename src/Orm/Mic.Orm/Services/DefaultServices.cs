using System.Security.Cryptography;

namespace Mic.Orm.Services;

/// <summary>Provides the default <see cref="ITimeService"/>: the machine's clock.</summary>
public class TimeService(EntityApp app) : ITimeService {
  protected EntityApp App { get; } = app;

  public virtual DateTime UtcNow => DateTime.UtcNow;
}

/// <summary>Provides a clock the application can shift, for tests and for reproducing what happened at a particular
/// hour.</summary>
/// <remarks>Not registered by default; an application opts in by registering it. Time still passes while the shift
/// is on.</remarks>
public class AdjustableTimeService(EntityApp app) : TimeService(app) {
  /// <summary>Gets or sets how far from real time this clock runs; positive is the future, negative the past.</summary>
  public TimeSpan TimeShift { get; set; }

  public override DateTime UtcNow => DateTime.UtcNow + TimeShift;
}

/// <summary>Provides the default <see cref="IGuidFactory"/>: the RFC 9562 layouts of <see cref="GuidScheme"/>.
/// </summary>
public class GuidFactory(EntityApp app) : IGuidFactory {
  protected EntityApp App { get; } = app;

  // The bits a Guid's place among those of its instant takes.
  const int SequenceBits = 12;
  const int SequenceMask = (1 << SequenceBits) - 1;

  /// <summary>Gets the application's clock, resolved per call so that a service registered later takes effect.
  /// </summary>
  protected ITimeService Time => App.TimeService;

  /// <summary>Creates a new Guid in a layout, stamped with the given instant or with the application's clock.
  /// </summary>
  /// <param name="scheme">The layout.</param>
  /// <param name="timestamp">The instant to stamp the Guid with, or <see langword="default"/> for now.</param>
  /// <param name="sequence">The place of the Guid among those made at the instant, counted from 0; or
  /// <see langword="null"/> for a Guid with no place among others.</param>
  /// <returns>The new Guid.</returns>
  /// <exception cref="ArgumentException"><paramref name="scheme"/> is <see cref="GuidScheme.Auto"/>.</exception>
  /// <remarks>Never reads the machine's clock, so a shifted application clock reaches the keys as well as the stamps.
  /// </remarks>
  public virtual Guid NewGuid(GuidScheme scheme, DateTime timestamp = default, int? sequence = null) {
    DateTimeOffset instant = timestamp == default ? Time.UtcNow : timestamp;
    // A count past its twelve bits carries into the time, a millisecond for every 4,096.
    if (sequence is { } place)
      instant = instant.AddMilliseconds(place >> SequenceBits);
    return scheme switch {
      GuidScheme.V7 => CreateV7(instant, sequence),
      GuidScheme.V8SqlServer => CreateSqlServerV8(instant, sequence),
      GuidScheme.V4 => Guid.NewGuid(),
      _ => throw new ArgumentException("Auto is resolved by the database; a factory is asked for a layout.",
        nameof(scheme)),
    };
  }

  /// <summary>Creates a version 7 UUID, with the place among the Guids of its instant in the twelve bits after the
  /// time, which a server that compares bytes first to last reads next.</summary>
  private static Guid CreateV7(DateTimeOffset instant, int? sequence) {
    var guid = Guid.CreateVersion7(instant);
    if (sequence is not { } place)
      return guid;
    Span<byte> bytes = stackalloc byte[16];
    guid.TryWriteBytes(bytes, bigEndian: true, out _);
    var count = place & SequenceMask;
    bytes[6] = (byte)(0x70 | (count >> 8));
    bytes[7] = (byte)count;
    return new Guid(bytes, bigEndian: true);
  }

  /// <summary>Creates a version 8 UUID whose 48-bit millisecond timestamp sits in the six bytes SQL Server compares
  /// first, so that its order to that server's index is chronological, with the place among the Guids of its
  /// instant in the two bytes that server compares second.</summary>
  private static Guid CreateSqlServerV8(DateTimeOffset instant, int? sequence) {
    Span<byte> bytes = stackalloc byte[16];
    RandomNumberGenerator.Fill(bytes);

    // The last six bytes, most significant first, so that byte comparison is chronological comparison.
    var milliseconds = (ulong)instant.ToUnixTimeMilliseconds();
    for (var i = 0; i < 6; i++)
      bytes[10 + i] = (byte)(milliseconds >> (40 - 8 * i));

    // The two fields RFC 9562 pins: version 8 in the high nibble of byte 6, and the variant's 10 in the top bits of
    // byte 8.
    bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);
    bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);

    // The count under the variant's two bits, the two bits between them left at zero so that the count alone orders.
    if (sequence is { } place) {
      var count = place & SequenceMask;
      bytes[8] = (byte)(0x80 | (count >> 8));
      bytes[9] = (byte)count;
    }

    // Big-endian, since the offsets above are RFC positions and .NET's own layout orders the first three groups
    // differently; ConvertHelper.ConvertValue reads them back the same way.
    return new Guid(bytes, bigEndian: true);
  }
}
