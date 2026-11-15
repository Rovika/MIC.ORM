namespace BeautyShop;

// THE WORDS THE SEED IS MADE OF, kept apart from the code that arranges them.
//
// SMALL POOLS COMBINED, rather than long lists of everything: five thousand rows want variety more than they want a
// dictionary, and two hundred first names against two hundred surnames is forty thousand distinct people. It also
// keeps the file readable, which a generated name list would not be.
internal static class BeautySeedData {
  public static readonly string[] CompanyNames = [
    "Gloss & Co", "Velvet Room", "Studio Lumen", "The Nail Atelier", "Aurora Beauty Bar",
  ];

  public static readonly string[] ShopWords = [
    "Downtown", "Riverside", "Northgate", "Old Town", "Harbour", "Parkway", "Westfield",
  ];

  public static readonly string[] FirstNames = [
    "Ana", "Bea", "Chloe", "Dana", "Elif", "Farah", "Grace", "Hana", "Iris", "Jana", "Kira", "Lena", "Mira", "Nadia",
    "Olga", "Petra", "Rosa", "Sara", "Tara", "Vera", "Adam", "Boris", "Caleb", "Denis", "Emil", "Felix", "Gabor",
    "Hugo", "Ivan", "Jonas", "Karl", "Lukas", "Milan", "Nico", "Oscar", "Pavel", "Rafael", "Stefan", "Tomas", "Viktor",
  ];

  public static readonly string[] LastNames = [
    "Adler", "Bauer", "Costa", "Duval", "Engel", "Fischer", "Garcia", "Horvath", "Ibsen", "Jansen", "Kovacs", "Lang",
    "Moreau", "Novak", "Olsen", "Petrov", "Quinn", "Rossi", "Silva", "Tanaka", "Ueda", "Varga", "Weber", "Yilmaz",
    "Zeman", "Almeida", "Brandt", "Cerny", "Dubois", "Eriksen",
  ];

  public static readonly string[] Cities = [
    "Riverton", "Ashford", "Belmont", "Clearwater", "Dunmore", "Eastvale", "Fairhaven", "Glenwood",
  ];

  public static readonly string[] Streets = [
    "Market Street", "Chapel Lane", "Rowan Avenue", "Linden Way", "Harbour Road", "Mill Court", "Ash Grove",
  ];

  /// <summary>A service menu, as (category, specialties, then the treatments in it).</summary>
  /// <remarks>Specialties are on the CATEGORY, which is what matches a category to the stylists and stations able to
  /// work in it — so the seed uses the same match rather than assigning at random, and a booking is one a real shop
  /// could have taken.</remarks>
  public static readonly (string Category, Specialties Specialties, string[] Services)[] Menu = [
    ("Hair", Specialties.Hair, ["Cut & Finish", "Blow Dry", "Restyle", "Fringe Trim"]),
    ("Colour", Specialties.Colour | Specialties.Hair, ["Full Colour", "Highlights", "Balayage", "Root Touch-Up"]),
    ("Nails", Specialties.Nails, ["Classic Manicure", "Gel Manicure", "Pedicure", "Nail Art"]),
    ("Lashes & Brows", Specialties.Lashes | Specialties.Brows, ["Lash Lift", "Lash Extensions", "Brow Shape", "Brow Tint"]),
    ("Skin", Specialties.Skincare, ["Express Facial", "Deep Cleanse", "Peel"]),
    ("Massage", Specialties.Massage, ["Back & Shoulders", "Full Body", "Scalp Massage"]),
  ];

  public static readonly string[] VendorNames = [
    "Northwind Salon Supply", "Lumière Distribution", "PureForm Labs", "Atelier Wholesale", "Bright Leaf Trading",
  ];

  /// <summary>Product categories, and the goods in each — the retail half of a salon's takings.</summary>
  public static readonly (string Category, string[] Products)[] Catalogue = [
    ("Hair Care", ["Repair Shampoo", "Volume Conditioner", "Leave-In Serum", "Heat Shield"]),
    ("Colour Supplies", ["Developer 20 Vol", "Toner Sachet", "Bleach Powder", "Colour Brush"]),
    ("Nail Care", ["Gel Base Coat", "Gel Top Coat", "Cuticle Oil", "Nail File Pack"]),
    ("Skin Care", ["Cleansing Milk", "Hydrating Mask", "SPF 30 Fluid"]),
    ("Tools", ["Shears 5.5in", "Sectioning Clips", "Round Brush", "Towel Set"]),
  ];

  public static readonly string[] TimeZones = [
    "Europe/Berlin", "Europe/Prague", "Europe/Warsaw", "Europe/Vienna",
  ];
}
