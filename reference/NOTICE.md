# Third-party attribution: Anova reference implementation

## What this file covers

`anova_interactive_reference.py` in this directory is **not** part of AnovaDesk. It is
Anova's own reference implementation of their Wi-Fi developer API, retained here so the
protocol shapes implemented by AnovaDesk can be checked against the vendor's example.

It is never compiled, executed, or redistributed as part of the AnovaDesk binary.

## Origin

| Field | Value |
| --- | --- |
| Title | Anova WiFi Device Controller (`anova_interactive.py`) |
| Author | Anova Applied Electronics, Inc. |
| Source | https://github.com/anova-culinary/developer-project-wifi |
| Documentation | https://developer.anovaculinary.com/docs/devices/wifi/implementation-example |
| Retrieved | 2026-10-30 |
| File in this repository | `reference/anova_interactive_reference.py` (renamed, contents unmodified) |

## Licensing status

Anova's developer documentation states:

> "This implementation example is available under the MIT License. For more details,
> see the LICENSE file."

**However**, as retrieved on 2026-10-30, the linked repository contains only `README.md`
and `anova_interactive.py`. There is no `LICENSE` file, and the `README.md` contains no
licence statement. No copyright holder line or year has been published for this code.

Because of that, this notice cannot reproduce a copyright notice that does not exist.
It records the vendor's stated licence, the exact origin, and the retrieval date instead.
If Anova publishes a `LICENSE` file for that repository, this notice should be updated to
quote it verbatim.

## MIT License (as stated by Anova)

The following is the standard MIT text, which Anova's documentation says applies. The
`<copyright holder>` line is absent from the upstream repository and is left explicit
rather than invented.

```
MIT License

Copyright (c) <year> <copyright holder> - see note above; not published upstream

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Relationship to this project's licence

AnovaDesk's own source code is released under the Unlicense (see `../LICENSE`). That
dedication applies only to original code in this repository. The contents of this
`reference/` directory are third-party material and are **not** covered by the Unlicense.

## Trademarks

Anova, Anova Culinary, Anova Precision Cooker, Anova Precision Oven and the Anova logo
are trademarks or registered trademarks of Anova Applied Electronics, Inc. They are used
here only to describe interoperability. This project is not affiliated with, endorsed by,
sponsored by, or supported by Anova Applied Electronics, Inc.

## If you prefer to avoid the ambiguity

Deleting this directory removes all third-party material from the repository. AnovaDesk
does not depend on it at build time or run time; it is documentation only.
