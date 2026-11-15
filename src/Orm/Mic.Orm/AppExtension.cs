using Mic.Orm.Model;
using Mic.Orm.Model.Construction;
using Mic.Orm.Runtime;

namespace Mic.Orm;

/// <summary>Defines the core behavior of a cross-cutting rule, such as multi-tenancy or soft delete, that is added to
/// an application with <see cref="EntityApp.AddExtension"/>, and provides a base for derived classes.</summary>
/// <remarks><see cref="EntityApp.Init"/> calls the extensions in the order they were added, once the model is built
/// and on the application: <see cref="OnEntityBuilt"/> once per entity, where a filter can be attached with
/// <see cref="EntityTypeInfo.AddFilter"/>, and <see cref="OnModelConstructed"/> once with the finished model, after
/// the modules, where events are hooked. An extension owns no entities; it overrides only the members it uses.
/// </remarks>
public abstract class AppExtension {
  /// <summary>Gets the application this extension was added to, or <see langword="null"/> until
  /// <see cref="EntityApp.AddExtension"/> has set it.</summary>
  public EntityApp? App { get; internal set; }

  /// <summary>Gets or sets the builder context of the model build in progress, set by the builder around its calls
  /// to <see cref="OnEntityBuilt"/>, or <see langword="null"/> outside a build.</summary>
  internal BuilderContext? BuildContext { get; set; }

  /// <summary>Adds a warning to the build report of the model being built.</summary>
  /// <param name="message">The warning.</param>
  /// <param name="location">The place the warning is reported at, or <see langword="null"/> for none.</param>
  /// <exception cref="InvalidOperationException">No model is being built.</exception>
  protected void AddWarning(string message, string? location = null) =>
    (BuildContext ?? throw new InvalidOperationException("A warning is added while the model is built, from OnEntityBuilt."))
      .AddWarning(message, location);

  /// <summary>Determines whether a session has this extension disabled, so that its write hooks leave the session's
  /// records alone.</summary>
  /// <param name="session">The session saving.</param>
  /// <returns><see langword="true"/> if the session disabled this extension, a base of it, or every extension;
  /// otherwise, <see langword="false"/>.</returns>
  /// <remarks>The read side needs no call: the session leaves the filters of a disabled extension out of its queries
  /// by the owner type given to <see cref="EntityTypeInfo.AddFilter"/>. A write hook checks this first.</remarks>
  protected bool IsDisabledBy(EntitySession session) {
    ArgumentNullException.ThrowIfNull(session);
    return session.IsExtensionDisabled(GetType());
  }

  /// <summary>When overridden in a derived class, applies this extension to a finished entity, whose members, keys
  /// and references are resolved.</summary>
  /// <param name="entity">The entity that has just been built.</param>
  /// <remarks>Called for every entity of the model, in the order the model holds them. An exception thrown here
  /// becomes a diagnostic in the build report under this extension's name.</remarks>
  public virtual void OnEntityBuilt(EntityTypeInfo entity) { }

  /// <summary>When overridden in a derived class, hooks the events this extension listens to, on the entities'
  /// <see cref="EntityTypeInfo.Events"/> or the application's <see cref="EntityApp.Events"/>.</summary>
  /// <param name="model">The finished model, which built without errors.</param>
  public virtual void OnModelConstructed(EntityAppModel model) { }
}
