"""
MkDocs build hook: publish the repository-root ``doc-images/`` directory into the
built site.

Why this exists
---------------
The developer documentation references its screenshots with absolute URLs of the
form ``/doc-images/<name>.png`` (see, e.g.,
``docs/developer/entities/create-entity-field.md``), but the ``doc-images/``
directory lives at the **repository root**, i.e. *outside* the MkDocs
``docs_dir`` (``docs/``). MkDocs only copies files that live inside ``docs_dir``,
so a plain ``mkdocs build`` leaves those images uncopied and every
``/doc-images/*.png`` reference 404s in the built/deployed site (QA Issue #5 —
a pre-existing, site-wide condition affecting ~20 developer pages).

What it does
------------
On ``on_post_build`` it copies the repository-root ``doc-images/`` tree into
``<site_dir>/doc-images/`` so that the existing absolute references resolve. It
is:

* **Additive** — it never modifies page content or moves the source assets; the
  repository-root ``doc-images/`` is left exactly where it is.
* **Dependency-free** — it uses only MkDocs core (the ``hooks:`` mechanism,
  available since MkDocs 1.4) and the Python standard library.
* **Idempotent** — ``dirs_exist_ok=True`` makes repeated builds safe.

Source: /doc-images/ (repository root, 25 PNG assets); referenced by
docs/developer/**/*.md via absolute ``/doc-images/*.png`` links.
"""

from __future__ import annotations

import logging
import os
import shutil

log = logging.getLogger("mkdocs.hooks.doc_images")

_ASSET_DIR = "doc-images"


def on_post_build(config, **kwargs) -> None:
    """Copy repo-root ``doc-images/`` into the built ``site_dir``.

    Resolves the repository root from the location of the active ``mkdocs.yml``
    (``config_file_path``) so the hook is independent of the current working
    directory. No-ops safely when the source directory is absent.
    """
    config_file = config.get("config_file_path") or ""
    repo_root = (
        os.path.dirname(os.path.abspath(config_file)) if config_file else os.getcwd()
    )
    src = os.path.join(repo_root, _ASSET_DIR)
    if not os.path.isdir(src):
        log.warning("doc-images source not found at %s; skipping copy", src)
        return

    dst = os.path.join(config["site_dir"], _ASSET_DIR)
    shutil.copytree(src, dst, dirs_exist_ok=True)

    copied = sum(len(files) for _, _, files in os.walk(dst))
    log.info("doc-images: copied %d asset(s) into %s", copied, dst)
