using System.Collections;
using Mic.Orm.Model;

namespace Mic.Orm.Runtime;

/// <summary>Represents one row of a many-to-many list: the link record and the target record it points at.</summary>
public readonly record struct LinkRecordPair(EntityRecord Link, EntityRecord Target);

/// <summary>Defines what the session and the member accessors see of a list member, whatever entity it
/// holds.</summary>
public interface IMemberEntityList {
  EntityRecord Owner { get; }
  EntityMemberInfo Member { get; }

  /// <summary>Gets a value that indicates whether the rows have been fetched, which happens when the contents are
  /// first wanted.</summary>
  bool IsLoaded { get; }

  /// <summary>Gets a value that indicates whether the application has changed the list since it was loaded or last
  /// saved.</summary>
  bool IsModified { get; }

  void SetItems(IEnumerable<EntityRecord> records);

  /// <summary>Sets the items of a many-to-many list: the targets, each with the link row it was reached
  /// through.</summary>
  void SetLinkRecordPairs(IEnumerable<LinkRecordPair> pairs);

  void SetEmpty();

  /// <summary>Turns what changed in the list into records the save writes: link rows made and deleted, and positions
  /// renumbered.</summary>
  /// <remarks>The session calls this at the start of a save.</remarks>
  void PrepareForSave();

  /// <summary>Marks the list as what the database now holds, after a save.</summary>
  void AcceptChanges();
}

/// <summary>Represents the object behind a list member of an entity, such as <c>person.Phones</c> or
/// <c>book.Authors</c>.</summary>
/// <typeparam name="TEntity">The interface of the entities the list holds.</typeparam>
/// <remarks><para>Reading the member fetches nothing; the rows are loaded, synchronously, when the contents are first
/// asked for: the count, an item or an enumeration. A list on a new record, on a record the session has let go of or
/// on a record with no session is empty and loaded from the start.</para>
/// <para>A child added to a one-to-many list is pointed back at the owner when it points nowhere yet; one removed has
/// its reference back cleared where the model allows a null there, and is otherwise left as it is. A many-to-many
/// list is diffed against its loaded links at the save: a target without a link gets one made, a link whose target
/// has left the list is deleted, so an add followed by a remove writes nothing. A <c>[PersistOrderIn]</c> member is
/// renumbered at the save, one-based, in list order.</para></remarks>
public sealed class MemberEntityList<TEntity> : IMemberEntityList, IList<TEntity>, IReadOnlyList<TEntity> where TEntity : class {
  List<TEntity>? _items;
  Dictionary<EntityRecord, EntityRecord>? _links;

  public MemberEntityList(EntityRecord owner, EntityMemberInfo member) {
    ArgumentNullException.ThrowIfNull(owner);
    ArgumentNullException.ThrowIfNull(member);
    Owner = owner;
    Member = member;
  }

  public EntityRecord Owner { get; }
  public EntityMemberInfo Member { get; }
  public bool IsLoaded => _items != null;
  public bool IsModified { get; private set; }

  /// <summary>Gets the link record each target of a many-to-many list was reached through, keyed by the target's
  /// record.</summary>
  /// <value>The links, or <see langword="null"/> for a one-to-many list or until the list has loaded.</value>
  public IReadOnlyDictionary<EntityRecord, EntityRecord>? Links => _links;

  public int Count => Items.Count;
  public bool IsReadOnly => false;

  public TEntity this[int index] {
    get => Items[index];
    set {
      ArgumentNullException.ThrowIfNull(value);
      Owner.Session?.ThrowIfSaving();
      var replaced = Items[index];
      Items[index] = value;
      ClearInverseReference(replaced);
      Changed(value);
    }
  }

  // ----- what the session fills ---------------------------------------------------------------------------------------

  public void SetItems(IEnumerable<EntityRecord> records) {
    ArgumentNullException.ThrowIfNull(records);
    var items = new List<TEntity>();
    foreach (var record in records)
      items.Add((TEntity)(object)record.EntityInstance);
    _items = items;
    IsModified = false;
  }

  public void SetLinkRecordPairs(IEnumerable<LinkRecordPair> pairs) {
    ArgumentNullException.ThrowIfNull(pairs);
    var items = new List<TEntity>();
    var links = new Dictionary<EntityRecord, EntityRecord>();
    foreach (var (link, target) in pairs) {
      items.Add((TEntity)(object)target.EntityInstance);
      links[target] = link;
    }
    _items = items;
    _links = links;
    IsModified = false;
  }

  public void SetEmpty() {
    _items = [];
    if (Member.ListInfo?.Kind == ListKind.ManyToMany)
      _links = [];
    IsModified = false;
  }

  // ----- IList<T> ------------------------------------------------------------------------------------------------------

  public void Add(TEntity item) {
    ArgumentNullException.ThrowIfNull(item);
    Owner.Session?.ThrowIfSaving();
    Items.Add(item);
    Changed(item);
  }

  public void Insert(int index, TEntity item) {
    ArgumentNullException.ThrowIfNull(item);
    Owner.Session?.ThrowIfSaving();
    Items.Insert(index, item);
    Changed(item);
  }

  public bool Remove(TEntity item) {
    Owner.Session?.ThrowIfSaving();
    var removed = Items.Remove(item);
    if (removed) {
      ClearInverseReference(item);
      MarkModified();
    }
    return removed;
  }

  public void RemoveAt(int index) {
    Owner.Session?.ThrowIfSaving();
    var item = Items[index];
    Items.RemoveAt(index);
    ClearInverseReference(item);
    MarkModified();
  }

  public void Clear() {
    Owner.Session?.ThrowIfSaving();
    var items = Items.ToArray();
    Items.Clear();
    foreach (var item in items)
      ClearInverseReference(item);
    MarkModified();
  }

  public bool Contains(TEntity item) => Items.Contains(item);
  public int IndexOf(TEntity item) => Items.IndexOf(item);
  public void CopyTo(TEntity[] array, int arrayIndex) => Items.CopyTo(array, arrayIndex);
  public IEnumerator<TEntity> GetEnumerator() => Items.GetEnumerator();
  IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

  public override string ToString() => _items == null ? "(not loaded)" : $"{{Count = {_items.Count}}}";

  // ----- the save ------------------------------------------------------------------------------------------------------

  public void PrepareForSave() {
    if (!IsModified || _items == null || Member.ListInfo is not { } info || Owner.Session is not { } session)
      return;
    // A deleted or forgotten owner takes its lists with it: its link rows are the constraint's or the application's
    // business, and its children are not renumbered.
    if (Owner.Status is EntityStatus.Deleted or EntityStatus.Fantom)
      return;

    if (info.Kind == ListKind.ManyToMany)
      ReconcileLinks(info, session);

    if (info.PersistOrderMember is { } position)
      for (var i = 0; i < _items.Count; i++) {
        var record = EntityHelper.GetRecord(_items[i]);
        if (info.Kind == ListKind.ManyToMany)
          record = _links![record];
        record.SetValue(position, Convert.ChangeType(i + 1, position.DataType));
      }
  }

  public void AcceptChanges() => IsModified = false;

  /// <summary>Diffs the list against its loaded links: every target keeps or gets a link, and every link whose target
  /// is gone is deleted.</summary>
  /// <remarks>The links are brought up to date as it goes, so a second save over the same list starts from what is
  /// true.</remarks>
  private void ReconcileLinks(ListInfo info, EntitySession session) {
    var links = _links ??= [];
    var kept = new HashSet<EntityRecord>();
    foreach (var item in _items!) {
      var target = EntityHelper.GetRecord(item);
      if (!links.TryGetValue(target, out var link) || link.Status is EntityStatus.Deleted or EntityStatus.Fantom) {
        link = session.NewLinkRecord(info, Owner, target);
        links[target] = link;
      }
      kept.Add(target);
    }
    foreach (var (target, link) in links.ToArray())
      if (!kept.Contains(target)) {
        session.DeleteEntity(link.EntityInstance);
        links.Remove(target);
      }
  }

  // ----- loading -------------------------------------------------------------------------------------------------------

  private List<TEntity> Items {
    get {
      if (_items == null)
        Load();
      return _items!;
    }
  }

  private void Load() {
    // A new, forgotten or sessionless owner has nothing to fetch.
    if (Owner.Status is EntityStatus.New or EntityStatus.Fantom || Owner.Session is not { } session) {
      SetEmpty();
      return;
    }
    session.LoadList(this);
  }

  private void Changed(TEntity item) {
    MarkModified();
    Adopt(item);
  }

  /// <summary>Marks the list modified and registers it with the session on the first change only.</summary>
  private void MarkModified() {
    if (IsModified)
      return;
    IsModified = true;
    Owner.Session?.RegisterChangedList(this);
  }

  /// <summary>Points a child of a one-to-many list back at the owner where its reference back is empty.</summary>
  /// <remarks>The reference is read through the accessor, so an unresolved foreign key counts as pointing
  /// somewhere.</remarks>
  private void Adopt(TEntity item) {
    if (Member.ListInfo is not { Kind: ListKind.OneToMany, InverseMember: { } inverse })
      return;
    var record = EntityHelper.GetRecord(item);
    if (record.GetValue(inverse) == null)
      record.SetValue(inverse, Owner.EntityInstance);
  }

  /// <summary>Clears a removed child's reference back where the model allows a null there and it points at this
  /// owner.</summary>
  private void ClearInverseReference(TEntity item) {
    if (Member.ListInfo is not { Kind: ListKind.OneToMany, InverseMember: { IsNullable: true } inverse })
      return;
    var record = EntityHelper.GetRecord(item);
    if (ReferenceEquals(record.GetValue(inverse), Owner.EntityInstance))
      record.SetValue(inverse, null);
  }
}
