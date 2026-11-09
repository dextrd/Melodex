from pathlib import Path

from app.folders import clear_away, find_musicless, musicless, remove_musicless


def touch(path: Path, data: bytes = b"x") -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)
    return path


def test_a_folder_of_leftovers_junk_and_empty_folders_has_no_music(tmp_path):
    touch(tmp_path / "a" / "cover.jpg")
    touch(tmp_path / "a" / ".DS_Store")
    touch(tmp_path / "a" / "Thumbs.db")
    (tmp_path / "a" / "Artwork").mkdir()
    touch(tmp_path / "a" / "Scans" / "back.png")
    touch(tmp_path / "a" / "@eaDir" / "SYNOINDEX_MEDIA_INFO")
    assert musicless(tmp_path / "a")


def test_music_or_anything_unknown_keeps_a_folder(tmp_path):
    touch(tmp_path / "song" / "Disc 1" / "01.flac")
    touch(tmp_path / "video" / "making of.mkv")
    touch(tmp_path / "copy" / ".melodex-01.flac")
    (tmp_path / "synced" / ".stfolder").mkdir(parents=True)
    for name in ("song", "video", "copy", "synced"):
        assert not musicless(tmp_path / name), name


def test_clearing_away_moves_leftovers_to_where_the_music_went_and_goes_up(tmp_path):
    library = tmp_path
    old = library / "Unknown" / "Dummy (1994)"
    touch(old / "cover.jpg", b"old cover")
    touch(old / "Scans" / "inlay.png")
    touch(old / ".DS_Store")
    (old / "Disc 1").mkdir()
    new = library / "Portishead" / "Dummy"
    touch(new / "cover.jpg", b"new cover")

    removed = clear_away(old, library, new)

    assert removed == [old, library / "Unknown"]
    assert (new / "cover.jpg").read_bytes() == b"new cover"
    assert (new / "Scans" / "inlay.png").exists()
    assert not (new / ".DS_Store").exists()
    assert not (new / "Disc 1").exists()


def test_an_albums_leftovers_above_its_disc_folders_go_with_the_album(tmp_path):
    library = tmp_path
    touch(library / "old" / "Album" / "folder.jpg")
    (library / "old" / "Album" / "CD2").mkdir(parents=True)
    touch(library / "old" / "Album" / "CD2" / "02.flac")
    disc1 = library / "old" / "Album" / "CD1"
    disc1.mkdir()
    new = library / "Artist" / "Album"
    new.mkdir(parents=True)

    # Disc 2 still has its song, so only disc 1's folder goes.
    assert clear_away(disc1, library, new) == [disc1]
    (library / "old" / "Album" / "CD2" / "02.flac").unlink()
    # With the second disc's gone too, so does the album's folder, its picture going to the album's new folder, and
    # the folder above it, which had nothing else.
    disc2 = library / "old" / "Album" / "CD2"
    assert clear_away(disc2, library, new) == [disc2, library / "old" / "Album", library / "old"]
    assert (new / "folder.jpg").exists()


def test_leftovers_with_nowhere_to_go_are_deleted_and_the_library_stays(tmp_path):
    library = tmp_path / "music"
    touch(library / "a" / "b" / "cover.jpg")
    assert clear_away(library / "a" / "b", library) == [library / "a" / "b", library / "a"]
    assert library.is_dir() and not any(library.iterdir())


def test_a_folder_still_with_music_isnt_touched(tmp_path):
    touch(tmp_path / "a" / "01.flac")
    touch(tmp_path / "a" / "cover.jpg")
    assert clear_away(tmp_path / "a", tmp_path) == []
    assert (tmp_path / "a" / "cover.jpg").exists()


def test_the_sweep_finds_the_topmost_folders_with_no_music_and_removes_them(tmp_path):
    touch(tmp_path / "Artist" / "Album" / "01.flac")
    touch(tmp_path / "Artist" / "Old Album" / "cover.jpg")
    (tmp_path / "Gone" / "Empty" / "Emptier").mkdir(parents=True)
    touch(tmp_path / "Notes" / "booklet.pdf")
    (tmp_path / ".stfolder").mkdir()
    (tmp_path / "lost+found").mkdir()

    assert find_musicless(tmp_path) == [tmp_path / "Artist" / "Old Album", tmp_path / "Gone"]
    removed, failed = remove_musicless(tmp_path)
    assert (removed, failed) == ([tmp_path / "Artist" / "Old Album", tmp_path / "Gone"], [])
    assert sorted(p.name for p in tmp_path.iterdir()) == [".stfolder", "Artist", "Notes", "lost+found"]
    assert (tmp_path / "Artist" / "Album" / "01.flac").exists()


def test_the_sweep_leaves_an_albums_artwork_folders_but_not_its_empty_ones(tmp_path):
    touch(tmp_path / "Album" / "01.flac")
    touch(tmp_path / "Album" / "Scans" / "inlay.jpg")
    (tmp_path / "Album" / "Artwork").mkdir()
    touch(tmp_path / "Two Discs" / "CD1" / "01.flac")
    touch(tmp_path / "Two Discs" / "Booklet" / "page 1.png")
    touch(tmp_path / "Two Discs" / "Empty" / "Thumbs.db")

    assert find_musicless(tmp_path) == [tmp_path / "Album" / "Artwork", tmp_path / "Two Discs" / "Empty"]
