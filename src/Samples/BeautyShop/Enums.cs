using System.ComponentModel.DataAnnotations;

namespace BeautyShop;

// The Beauty Shop sample's ENUMS. Kept together because they are read together — a reviewer scanning the model wants
// the vocabulary in one place, not spread across the entities that happen to use each one.
//
// A MIX ON PURPOSE. Plain enums, [Flags] enums and [Display] captions all exist here because this model doubles as
// the demo the UI is built against: a drop-down, a radio list and a checkbox list each need a shape to render.

/// <summary>Whether a record is still in play. One enum for all of them: "active or not" is the same question
/// everywhere, and a per-entity copy would be a dozen identical types.</summary>
public enum RecordStatus { Active, Inactive, Archived }

/// <summary>WHICH POPULATION a login belongs to — not what it may do, which is `EmployeeRoles`.</summary>
public enum UserType {
  /// <summary>Manages the application itself, and belongs to no organisation.</summary>
  Admin,
  Employee,
  Customer,
}

/// <summary>What a member of staff may do. FLAGS, because one person commonly does several — an owner who also
/// styles is the ordinary case in a small shop.</summary>
[Flags]
public enum EmployeeRoles {
  None = 0,
  Admin = 1,
  Manager = 1 << 1,
  Stylist = 1 << 2,
  Assistant = 1 << 3,
  /// <summary>A contact of the company — the owner, the accountant — reached through the person's email and phone.
  /// </summary>
  CompanyContact = 1 << 4,
}

/// <summary>What a stylist is qualified to do, what a station is fitted for, and what a service category is. FLAGS,
/// and used in THREE places — which is why it is one enum rather than three lists.</summary>
[Flags]
public enum Specialties {
  None     = 0,
  Hair     = 1,
  Colour   = 1 << 1,
  Nails    = 1 << 2,
  Lashes   = 1 << 3,
  Brows    = 1 << 4,
  Skincare = 1 << 5,
  Massage  = 1 << 6,
}

public enum PhoneType { Mobile, Home, Office }

public enum AppointmentStatus {
  Requested, Confirmed, [Display(Name = "Checked In")] CheckedIn, Completed, Cancelled, [Display(Name = "No Show")] NoShow,
}

/// <summary>Why someone is not available — a holiday, illness, or simply not rostered.</summary>
public enum AvailabilityKind { Vacation, [Display(Name = "Sick Leave")] SickLeave, [Display(Name = "Time Off")] TimeOff, Training }

public enum PaymentMethod { Cash, [Display(Name = "Credit Card")] CreditCard, [Display(Name = "Debit Card")] DebitCard, [Display(Name = "Gift Card")] GiftCard, Transfer }

/// <summary>What a payment source IS — an organisation's own account, a card processor it settles through, or a till.
/// </summary>
public enum PaymentSourceKind { [Display(Name = "Bank Account")] BankAccount, [Display(Name = "Card Processor")] CardProcessor, [Display(Name = "Cash Drawer")] CashDrawer }

public enum ShipmentStatus { Ordered, Shipped, Received, Cancelled }

public enum InvoiceStatus { Draft, Issued, [Display(Name = "Partly Paid")] PartlyPaid, Paid, Overdue, Void }

public enum SaleStatus { Open, Completed, Refunded, Void }

public enum PayrollStatus { Draft, Approved, Paid }
