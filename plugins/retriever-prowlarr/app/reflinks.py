"""Copies that share their data on disk (reflinks), where the filesystem can make them.

A reflink is a second file using the same blocks as the first until either is changed, so tagging the copy leaves the
original as it was and only the changed blocks take space. btrfs, XFS made with reflink support and ZFS 2.2 or later
can make them, between folders on the same filesystem; anywhere else a copy is an ordinary copy.
"""

from __future__ import annotations

import errno
import fcntl
import os
import secrets
import shutil
from pathlib import Path

_FICLONE = 0x40049409


def clone(source: Path, target: Path) -> bool:
    """Copies source to target as a reflink if the filesystem can, or an ordinary copy if not. True for a reflink."""
    with source.open("rb") as src, target.open("wb") as dst:
        try:
            fcntl.ioctl(dst.fileno(), _FICLONE, src.fileno())
            shared = True
        except OSError:
            shutil.copyfileobj(src, dst, 1 << 20)
            shared = False
    shutil.copystat(source, target)
    return shared


def move(source: Path, target: Path) -> None:
    """Moves a file, as a reflink and a delete when it goes to another volume."""
    try:
        os.rename(source, target)
    except OSError as e:
        if e.errno != errno.EXDEV:
            raise
        clone(source, target)
        source.unlink()


def check(source_folder: Path, target_folder: Path) -> tuple[bool, str | None]:
    """Whether a file in one folder can be reflinked into the other, and why not if it can't, with a small file made
    for the purpose and removed again."""
    name = f".melodex-reflink-check-{secrets.token_hex(4)}"
    source, target = source_folder / name, target_folder / name
    try:
        source.write_bytes(b"\0" * 65536)
        return try_clone(source, target)
    finally:
        source.unlink(missing_ok=True)
        target.unlink(missing_ok=True)


def try_clone(source: Path, target: Path) -> tuple[bool, str | None]:
    """Reflinks source to target; whether it worked, and why not if it didn't. Leaves the target behind."""
    try:
        with source.open("rb") as src, target.open("wb") as dst:
            fcntl.ioctl(dst.fileno(), _FICLONE, src.fileno())
        return True, None
    except OSError as e:
        return False, reason(e)


def reason(error: OSError) -> str:
    if error.errno == errno.EXDEV:
        return "they're on different filesystems"
    if error.errno in (errno.EOPNOTSUPP, errno.ENOTTY, errno.EINVAL, errno.ENOSYS):
        return "the filesystem can't make them (btrfs, XFS made with reflink support and ZFS 2.2 or later can)"
    return error.strerror or str(error)
