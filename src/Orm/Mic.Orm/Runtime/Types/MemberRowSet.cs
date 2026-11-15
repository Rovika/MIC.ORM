using Mic.Linq;
using Mic.Linq.ExpressionTree;
using Mic.Linq.Nodes;
using Mic.Linq.Translation;
using Mic.Orm.Model;
namespace Mic.Orm.Runtime;

/// <summary>Defines the row set a queryable property of one owner hands back: the root of the expression the
/// application composes over, which stands for the target's rows tied to that owner.</summary>
/// <remarks>To LINQ the set is an entity set like any other, one constant at the bottom of the expression; to the
/// engine it stands for a query the ORM writes with <c>QueryMaker</c>, spliced in where the constant was. The set is
/// also the statement's argument: the scan registers it, and the root's parameter reads the owner off it when the
/// statement is bound, so every owner shares one statement.</remarks>
internal interface IMemberRowSet : IRowSet {
  EntityRecord Owner { get; }

  EntityMemberInfo Member { get; }

  /// <summary>Gets the node <see cref="IRowSet.GetNode"/> made last: where the caller's chain begins in the tree the
  /// converter built last, which the many-owners form is written over.</summary>
  QueryNode? Node { get; }
}

/// <summary>Represents the row set of a queryable property of one owner, typed by the target's interface.</summary>
internal sealed class MemberRowSet<T> : EntityQuery<T>, IMemberRowSet {
  public MemberRowSet(EntityQueryProvider provider, EntityTypeInfo entity, EntityRecord owner, EntityMemberInfo member)
    : base(provider, entity) {
    Owner = owner;
    Member = member;
  }

  public EntityRecord Owner { get; }

  public EntityMemberInfo Member { get; }

  public QueryNode? Node { get; private set; }

  /// <summary>Gets the cache key: a hash of the entity and member names, computed once per member.</summary>
  public override ulong CacheKey {
    get {
      var info = Member.ListInfo!;
      if (info.RowSetCacheKey == 0)
        info.RowSetCacheKey = Mic.Common.Fnv1aHash.Combine(Mic.Common.Fnv1aHash.Compute(Member.Entity!.Name), Member.Name) | 1;
      return info.RowSetCacheKey;
    }
  }

  /// <summary>Builds the query of the target's rows tied to the owner, with the owner read off this set as the
  /// statement's parameter.</summary>
  /// <returns>The query's root node.</returns>
  /// <remarks>The reader is static and kept with the cached statement, so it holds nothing of this call.</remarks>
  public override QueryNode GetNode() {
    var owner = QueryMaker.Parameter("owner", typeof(EntityRecord));
    owner.SetArgValueGetter(this, static rowSet => ((IMemberRowSet)rowSet).Owner);
    return Node = MemberQueryHelper.BuildListMemberQuery(Member, owner);
  }
}
