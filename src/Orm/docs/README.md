# The ORM documentation site

The sources of the public documentation of MIC ORM: a DocFX site of hand-written pages. There is no generated API
reference: the XML comments reach a developer through the IDE and the package, and an assistant through
`llms-full.txt` and the source. The sources live here, next to the ORM, and travel with it to the published ORM
repository, from which GitHub Pages serves the site.

## Building

DocFX is a local tool in the repository's manifest (`.config/dotnet-tools.json`); `dotnet tool restore` once on a
new machine.

```powershell
./src/Orm/docs/build.ps1            # builds _site: the pages, llms-full.txt
./src/Orm/docs/build.ps1 -Serve     # the same, then serves it on http://localhost:8086
```

From a shell that is not PowerShell: `powershell -File src/Orm/docs/build.ps1 -Serve` (`pwsh` where PowerShell 7
is installed).

`dotnet docfx src/Orm/docs/docfx.json --serve` does the same without `llms-full.txt`. `_site/` is not committed.

## Publishing

`.github/workflows/orm-docs.yml` builds the site on a push to `main` that touches the docs and deploys it to
GitHub Pages. The job runs only where the repository variable `ORM_DOCS_PAGES` is `true`; the published ORM
repository sets it, this one does not. Pages must be set to "GitHub Actions" as the source in the repository's
settings. The build needs the docs folder and `.config/dotnet-tools.json` only; it does not compile the ORM.

## Layout

| | |
|---|---|
| `docfx.json` | The content, the template (`modern`), the site's name. |
| `index.md`, `toc.yml` | The landing page and the top navigation. |
| `guide/` | The pages, one per subject, `toc.yml` giving the order and the sections, which is also the order of `llms-full.txt`. |
| `llms.txt` | The index for an assistant: what the site is, where the pages are, where the full text is. Kept by hand. |
| `build.ps1` | The build, and `llms-full.txt`. |

## Writing for the two readers

The site is read by people and by assistants fetching it for a developer who asked "how do I … in MIC". The
pages are written for both:

* **Each page stands alone.** It names its subject in the first line and shows a complete snippet, not a change
  to the previous page's. No "as shown above".
* **Samples are complete.** Usings, the attribute, the call, as they would be typed; no `...` in the middle. A
  sample an assistant reproduces must compile.
* **The exact names.** Attributes, methods, config keys and option values spelled as in the code, and repeated
  rather than paraphrased.
* **Differences from Entity Framework**, where another ORM's habit would mislead: a plain list at the end of a
  page whose subject has an EF counterpart; the Overview page carries the general one.
* **Plain CommonMark.** DocFX extensions (`[!NOTE]`, `[!code-csharp[]]`, xref) only where they earn it, since
  `llms-full.txt` is the raw markdown.
