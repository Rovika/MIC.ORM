using System.Buffers.Binary;
using System.Globalization;

namespace Mic.Common;

/// <summary>Represents a point on the Earth by its latitude and longitude, in degrees of WGS 84.</summary>
/// <param name="Latitude">The latitude, -90 to 90.</param>
/// <param name="Longitude">The longitude, -180 to 180.</param>
/// <remarks>A database holds a member of this type in its geographic point type, where it has one, and the value
/// travels to and from it as well-known binary; a server without such a type stores the binary.</remarks>
public readonly record struct GeoPoint(double Latitude, double Longitude) {
  /// <summary>The spatial reference system identifier of WGS 84, which GPS and every map uses.</summary>
  public const int Srid = 4326;

  const int WkbLength = 21;
  const uint WkbPoint = 1;
  const uint WkbSridFlag = 0x20000000;

  /// <summary>Writes the point as well-known binary: little-endian, the point type, X the longitude and Y the
  /// latitude.</summary>
  /// <returns>The 21 bytes.</returns>
  public byte[] ToWkb() {
    var wkb = new byte[WkbLength];
    wkb[0] = 1;
    BinaryPrimitives.WriteUInt32LittleEndian(wkb.AsSpan(1), WkbPoint);
    BinaryPrimitives.WriteDoubleLittleEndian(wkb.AsSpan(5), Longitude);
    BinaryPrimitives.WriteDoubleLittleEndian(wkb.AsSpan(13), Latitude);
    return wkb;
  }

  /// <summary>Reads a point out of well-known binary of either byte order, with or without the SRID PostGIS writes
  /// after the type.</summary>
  /// <param name="wkb">The binary.</param>
  /// <returns>The point.</returns>
  /// <exception cref="FormatException">The binary is not a point.</exception>
  public static GeoPoint FromWkb(ReadOnlySpan<byte> wkb) {
    if (wkb.Length < WkbLength)
      throw new FormatException($"Well-known binary of {wkb.Length} bytes is too short for a point.");
    var little = wkb[0] == 1;
    var type = little ? BinaryPrimitives.ReadUInt32LittleEndian(wkb[1..]) : BinaryPrimitives.ReadUInt32BigEndian(wkb[1..]);
    var at = (type & WkbSridFlag) != 0 ? 9 : 5;
    if ((type & ~WkbSridFlag) != WkbPoint)
      throw new FormatException($"Well-known binary of geometry type {type & ~WkbSridFlag} is not a point.");
    if (wkb.Length < at + 16)
      throw new FormatException($"Well-known binary of {wkb.Length} bytes is too short for a point.");
    var longitude = little ? BinaryPrimitives.ReadDoubleLittleEndian(wkb[at..]) : BinaryPrimitives.ReadDoubleBigEndian(wkb[at..]);
    var latitude = little ? BinaryPrimitives.ReadDoubleLittleEndian(wkb[(at + 8)..]) : BinaryPrimitives.ReadDoubleBigEndian(wkb[(at + 8)..]);
    return new GeoPoint(latitude, longitude);
  }

  public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Latitude}, {Longitude}");
}
