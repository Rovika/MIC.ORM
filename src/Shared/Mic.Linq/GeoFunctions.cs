using Mic.Common;

namespace Mic.Linq;

/// <summary>Provides the functions over geographic points that a query writes as the server's own, and that run
/// in memory too, for the entity cache and for plain code.</summary>
public static class GeoFunctions {
  /// <summary>The mean radius of the Earth in metres, the sphere the in-memory distance is measured on.</summary>
  public const double EarthRadius = 6_371_008.8;

  /// <summary>Returns the distance between two points along the Earth's surface, in metres.</summary>
  /// <param name="point">The first point.</param>
  /// <param name="other">The second point.</param>
  /// <returns>The distance in metres.</returns>
  /// <remarks>Inside a query the server measures it on its own figure of the Earth; in memory it is the great-circle
  /// distance on a sphere, within a fraction of a percent of the server's.</remarks>
  public static double Distance(GeoPoint point, GeoPoint other) {
    var latitude = ToRadians(point.Latitude);
    var otherLatitude = ToRadians(other.Latitude);
    var halfLatitude = Math.Sin((otherLatitude - latitude) / 2);
    var halfLongitude = Math.Sin(ToRadians(other.Longitude - point.Longitude) / 2);
    var chord = halfLatitude * halfLatitude + Math.Cos(latitude) * Math.Cos(otherLatitude) * halfLongitude * halfLongitude;
    return 2 * EarthRadius * Math.Asin(Math.Sqrt(chord));
  }

  private static double ToRadians(double degrees) => degrees * Math.PI / 180;
}
