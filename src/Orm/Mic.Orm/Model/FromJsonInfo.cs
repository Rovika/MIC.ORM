namespace Mic.Orm.Model;

/// <summary>Represents where a member the database computes reads its value from: a path inside the document of a
/// JSON member of the same entity. This class cannot be inherited.</summary>
/// <param name="JsonMember">The JSON member whose document holds the value.</param>
/// <param name="Path">The path of the value inside the document, as <c>$.a.b</c>.</param>
/// <param name="Stored"><see langword="true"/> if the server is to store the value rather than compute it on each
/// read; otherwise, <see langword="false"/>.</param>
public sealed record FromJsonInfo(EntityMemberInfo JsonMember, string Path, bool Stored);
