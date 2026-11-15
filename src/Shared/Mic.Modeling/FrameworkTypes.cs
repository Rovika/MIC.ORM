namespace Mic.Modeling;

/// <summary>Provides the structures the framework itself defines, which every application can use and derive from
/// but none may edit.</summary>
/// <remarks><see cref="AddTo"/> injects the structures into each data model as it is registered, so that one lookup
/// serves the explorer, the type pickers and base-type resolution, and a structure authored in an extension can name
/// <c>SearchRequest</c> as its base. <c>SearchRequest</c> has no filter field: a filter's shape is
/// application-specific, so an application derives from it and adds a filter typed to its own structure.</remarks>
public static class FrameworkTypes {
  public const string SearchRequest = "SearchRequest";
  public const string SortSpec = "SortSpec";

  /// <summary>Represents the name of the paging field of a search request that says how many rows to skip; a grid
  /// paging through its data source writes it.</summary>
  public const string SkipField = "Skip";
  /// <summary>Represents the name of the paging field of a search request that says how many rows to take; a grid
  /// paging through its data source writes it.</summary>
  public const string TakeField = "Take";

  /// <summary>Represents the name of the field that says what a search request asks for, as a type name.</summary>
  /// <remarks>The request envelope names no data target, so this field is how the server learns it. A result is not
  /// always an entity; an aggregate or a projection is one too.</remarks>
  public const string ResultTypeField = "ResultType";

  /// <summary>Adds the framework structures to a model, unless they are already there.</summary>
  /// <param name="model">The model to add to.</param>
  /// <returns>The model, for chaining.</returns>
  public static DataModel AddTo(DataModel model) {
    if (model.FindEntity(SearchRequest) != null)
      return model;

    // SortSpec first; SearchRequest.Sort references it.
    var sortSpec = new EntityInfo(SortSpec, EntityKind.Framework);
    sortSpec.AddField(new Field("Field", Scalar<string>()));      // the field path to sort by
    sortSpec.AddField(new Field("Desc", Scalar<bool>()));         // descending; absent/false = ascending
    model.AddEntity(sortSpec);

    var request = new EntityInfo(SearchRequest, EntityKind.Framework);
    // The value is a type name; the semantic lets a designer offer a picker over the model's types.
    var resultType = new Field(ResultTypeField, Scalar<string>());
    resultType.SetMetaData(KnownMetaKeys.Semantic, KnownSemantics.EntityName);
    request.AddField(resultType);
    request.AddField(new Field(SkipField, Scalar<int>()));
    request.AddField(new Field(TakeField, Scalar<int>()));
    request.AddField(new Field("Sort", new ListTypeRef(sortSpec.TypeRef)));
    model.AddEntity(request);

    return model;
  }

  private static ScalarTypeRef Scalar<T>() => new(typeof(T));
}
