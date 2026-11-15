# MIC ORM Overview

**MIC ORM is a .NET ORM.** An ORM (Object-Relational Mapper) is the data access layer of an application whose
data lives in a relational database: it converts rows to objects and objects to rows, and runs the SQL.

ORMs come in two flavors:

* **Lightweight**, such as Dapper: a thin layer of helpers over the database driver, with the SQL written by hand.
* **Full-featured**, such as Entity Framework Core and NHibernate: an abstracted data layer that generates most
  of the SQL and handles queries, updates, transactions and more, usually for more than one database server.

**MIC ORM is a full-featured ORM.** It has a full LINQ engine for querying data, entity tracking, a powerful and
flexible solution for database schema management, and a single abstracted data model mapped to multiple database
servers. Some of its advanced features are found in no other ORM. [Feature highlights](highlights.md) describes
them.

**MIC ORM is part of MIC, a larger application framework** to be released after the ORM. MIC stands for Minimize
Incremental Code, a principle to be explained when MIC is released. MIC ORM can be used as a standalone ORM in any
.NET application, without the rest of MIC.

**MIC ORM is the successor of [VITA](https://github.com/rivantsov/vita)**, an open-source .NET ORM by the same
author, used in production for years. It inherits many of VITA's unique features. It is a complete rewrite; no VITA
code is carried over. [Migrating from VITA](migrating-from-vita.md) lists what changes, and the steps.

**MIC ORM encapsulates years of experience** of building data-connected enterprise applications. The ORMs
available lacked the depth and the practical features a large application needs: thousands of tables, thousands
of forms and business functions over them. MIC ORM is built for that scale.

See more:

* [Simple application](simple-application.md): all the parts in one program.
* [Getting started](getting-started.md): creating the initial ORM application.
* [Feature highlights](highlights.md): the features in short.
* [Application data model](data-model.md): entities, keys, references, lists, modules.
