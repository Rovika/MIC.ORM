using Mic.Linq.Nodes;

namespace Mic.Orm.Model;

/// <summary>Represents one condition an extension attaches to every use of an entity, and how its cache blocks
/// differ.</summary>
/// <param name="Template">The condition, built once over a placeholder row of the entity, which each database
/// instantiates over its table's row when its db model is built.</param>
/// <param name="Discriminator">A function that tells one cache block from another, given the operation context, or
/// <see langword="null"/> when this filter cannot make two blocks differ; the entity cache keys its blocks by the
/// discriminators of the filters that vary.</param>
/// <param name="Varies"><see langword="true"/> if the condition reads the operation context, as for a tenant id;
/// otherwise, <see langword="false"/>. Decided once, at the declaration, from the template.</param>
/// <param name="Owner">The type of the extension that attached the filter, which a query names to leave the
/// filter out, or <see langword="null"/> for a filter attached by the application directly.</param>
public sealed record EntityFilter(QueryTreeTemplate Template, Func<object?, string>? Discriminator, bool Varies,
  Type? Owner = null);
