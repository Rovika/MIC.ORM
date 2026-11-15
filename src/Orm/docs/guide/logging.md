# Logging

A developer tuning a page wants to see every SQL statement the ORM ran for it, with its parameters and timings;
a production incident needs the same for the one request that failed, and nothing for the thousands that did
not. An ORM's log serves both when it is collected per operation, switched by mode, and handed to whatever
logging pipeline the application runs.

## The operation's log

Each operation collects a log: the SQL commands it ran, with their parameters, row counts and timings, the
messages the application logged, and the errors. `EntityAppSettings.LogMode` sets the mode every new context
starts with. `OperationContext.LogMode` changes it for one operation; `session.EnableLog(false)` switches the
operation's log off. `LogMode.Details` records every command and message; `LogMode.OnError` records them and
posts them only when an error occurs. At the end of the operation the entries go to the application's
`LogService`, which hands them in batches to its listeners. `session.GetLastCommands()` holds the commands the
last request ran, with row counts and timings, for a test or a debugger.

## The file log

Out of the box the ORM writes the log to a file: `app.SetLogFilePath("shop.log")` adds a file listener and sets
`Details`, and the file shows every SQL statement the application ran. This is a development and debugging
facility, not a production one: the file is deleted at the start, never rotated and never capped, and every
command is recorded. It should never be used in production.

## Logging in production

The suggested way to log in production is through the application's own logging pipeline: implement
`ILogListener`, one method called on a background thread with one operation's entries at a time, forward the
entries to the pipeline there, and add the listener with `app.LogService.AddListener`. An entry carries its
`Severity`, `Time`, `Location` and `Source`, the session, correlation and web request ids the host set on the
context, and renders itself with `WriteText`. With `LogMode.OnError` the listener gets a batch only for an
operation that failed, with every command that led to the error.
