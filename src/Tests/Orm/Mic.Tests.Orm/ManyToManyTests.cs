using Mic.Orm;
using Mic.Orm.Model;

namespace Mic.Tests.Orm;

// Many-to-many: `book.Authors` is two hops, not one — Book to the LINK entity, and from there to Author.
//
// The link stays an ordinary entity with an ordinary table rather than being hidden behind the list, because in
// practice it carries data of its own (when the author was added, in what order) and an ORM that conceals it leaves
// nowhere to put that.
[TestClass]
public class ManyToManyTests {
  private interface IBook {
    [PrimaryKey] Guid Id { get; set; }
    string Title { get; set; }
    [ManyToMany(typeof(IBookAuthor))] IList<IAuthor> Authors { get; }
  }

  private interface IAuthor {
    [PrimaryKey] Guid Id { get; set; }
    string Name { get; set; }
    [ManyToMany(typeof(IBookAuthor))] IList<IBook> Books { get; }
  }

  [PrimaryKey("Book,Author")]
  private interface IBookAuthor {
    IBook Book { get; set; }
    IAuthor Author { get; set; }
    int Position { get; set; }              // the data a hidden link table would have nowhere to put
  }

  private static EntityApp Build(params Type[] types) {
    var app = new EntityApp("App", new Version(1, 0));
    var module = new EntityModule("M", new Version(1, 0));
    module.RegisterEntities(types);
    app.AddModule(module, "s");
    app.TryInit();
    return app;
  }

  private static EntityApp Library() => Build(typeof(IBook), typeof(IAuthor), typeof(IBookAuthor));

  [TestMethod]
  public void AListThroughALinkEntityIsResolvedBothWays() {
    var app = Library();
    Assert.IsTrue(app.Initialized, app.Report());
    var model = app.Model!;
    var link = model.FindEntity("BookAuthor")!;
    var authors = model.FindEntity("Book")!.GetMember("Authors").ListInfo!;

    Assert.AreEqual(ListKind.ManyToMany, authors.Kind);
    Assert.AreSame(model.FindEntity("Author"), authors.TargetEntity);   // the FAR entity, not the link
    Assert.AreSame(link, authors.LinkEntity);
    Assert.AreSame(link.GetMember("Book"), authors.InverseMember);      // the leg back to this entity
    Assert.AreSame(link.GetMember("Author"), authors.OtherMember);      // and the leg out to the target

    // ----- and the OTHER end resolves to the same link, the other way round -------------------------------------------
    var books = model.FindEntity("Author")!.GetMember("Books").ListInfo!;

    Assert.AreSame(model.FindEntity("Book"), books.TargetEntity);
    Assert.AreSame(link.GetMember("Author"), books.InverseMember);
    Assert.AreSame(link.GetMember("Book"), books.OtherMember);

    // ----- while the LINK is an ordinary entity with its own table and its own data -------------------------------------
    //
    // Its references generated real foreign-key columns, and its own primary key is over both of them.
    Assert.IsNotNull(link.FindMember("Book_Id"));
    Assert.IsNotNull(link.FindMember("Author_Id"));
    Assert.IsNotNull(link.FindMember("Position"));
    CollectionAssert.AreEqual(new[] { "Book_Id", "Author_Id" },
      link.PrimaryKey!.MembersExpanded.Select(m => m.Member.Name).ToArray());
    Assert.IsTrue(link.PrimaryKey.IsComposite);
  }

  // ----- both ends the same entity ----------------------------------------------------------------------------------

  private interface IPerson {
    [PrimaryKey] Guid Id { get; set; }
    [ManyToMany(typeof(IFriendship), ThisEntityRef = nameof(IFriendship.From))] IList<IPerson> Friends { get; }
  }

  private interface IFriendship {
    [PrimaryKey] Guid Id { get; set; }
    IPerson From { get; set; }
    IPerson To { get; set; }
  }

  [TestMethod]
  public void NamingOneEnd_SettlesBothForASelfLink() {
    // A person's friends: the link points at the same entity twice, so nothing can be inferred. Resolving "back to
    // this" first lets the other leg exclude it — which is why naming one end is enough.
    var app = Build(typeof(IPerson), typeof(IFriendship));
    Assert.IsTrue(app.Initialized, app.Report());
    var link = app.Model!.FindEntity("Friendship")!;
    var friends = app.Model.FindEntity("Person")!.GetMember("Friends").ListInfo!;

    Assert.AreSame(link.GetMember("From"), friends.InverseMember);
    Assert.AreSame(link.GetMember("To"), friends.OtherMember);
  }

  // ----- what is reported -------------------------------------------------------------------------------------------

  private interface IUnregisteredLink {
    [PrimaryKey] Guid Id { get; set; }
    [ManyToMany(typeof(IBookAuthor))] IList<IAuthor> Authors { get; }
  }

  private interface IStrayLink {
    IBook Book { get; set; }
    [PrimaryKey] Guid Id { get; set; }
  }

  private interface IWrongLink {
    [PrimaryKey] Guid Id { get; set; }
    [ManyToMany(typeof(IStrayLink))] IList<IAuthor> Authors { get; }
  }

  // Both legs point at the same entity and neither is named — nothing can be inferred.
  private interface IAmbiguousRow {
    [PrimaryKey] Guid Id { get; set; }
    [ManyToMany(typeof(IAmbiguousLink))] IList<IAmbiguousRow> Others { get; }
  }

  private interface IAmbiguousLink {
    [PrimaryKey] Guid Id { get; set; }
    IAmbiguousRow Left { get; set; }
    IAmbiguousRow Right { get; set; }
  }

  [TestMethod]
  public void Link_TheBuilderCannotFollowIsReported() {
    // ----- one that was never registered ----------------------------------------------------------------------------
    var unregistered = Build(typeof(IUnregisteredLink), typeof(IAuthor));
    Assert.IsFalse(unregistered.Initialized);
    Assert.IsTrue(unregistered.Diagnostics().Any(d => d.Message.Contains("not a registered entity")));

    // ----- one that does not reach the target -------------------------------------------------------------------------
    var stray = Build(typeof(IWrongLink), typeof(IStrayLink), typeof(IBook), typeof(IAuthor), typeof(IBookAuthor));
    Assert.IsFalse(stray.Initialized);
    Assert.IsTrue(stray.Diagnostics().Any(d => d.Message.Contains("has no reference to")));

    // ----- and one whose two legs cannot be told apart, which names both and the attribute that settles it -------------
    var ambiguous = Build(typeof(IAmbiguousRow), typeof(IAmbiguousLink));
    Assert.IsFalse(ambiguous.Initialized);
    var message = ambiguous.Diagnostics().First(d => d.Message.Contains("more than once")).Message;
    Assert.IsTrue(message.Contains("Left") && message.Contains("Right"));
    Assert.IsTrue(message.Contains("ThisEntityRef"));
  }
}
