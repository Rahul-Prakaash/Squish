Squish
======

Turns files and folders into lossless archives. Works completely offline.

Use
  1. Run Squish.exe (no install needed).
  2. Drag files or folders onto the window, or use Add Folder / Add Files.
  3. Check the predicted size, pick a compression level, press Start.

Each item becomes its own .7z next to the original (or in a folder you
choose). Your original files are never changed or deleted.

All in one archive
  Switch this on to pack everything in the queue into a single archive. When
  you press Start you are asked where to save it and what to call it.
  Deleting that archive from the app puts its items back in the queue.

Lossless, always
  Every archive is proven before it is kept. This cannot be turned off:
    1. Compress.
    2. Fully decompress the archive and hash every file with SHA-256.
    3. Hash the original files the same way and compare: contents, names,
       file count, folder count and total size must all match.
  If anything differs, or some files could not be read (for example because
  another program is using them), the archive is removed and the item is
  marked as failed. Double-click an item to see its verification report.

Managing archives
  Finished items have two buttons:
    Show    - opens the archive's folder with it selected.
    Delete  - sends the archive to the Recycle Bin (after asking). The
              original files are not touched, and the item goes back into
              the queue so you can compress it again.

Size prediction
  Before you start, an evenly spread sample of each item is compressed with
  the same settings and the result is scaled up. In testing it was usually
  within about 5%. Folders of many similar small files usually end up
  smaller than predicted, never much bigger.

  How much you save depends on the content. Text, documents, code and
  programs shrink a lot. Video, music, photos and existing zip files are
  already compressed and barely shrink.

Advanced settings
  The Advanced button opens the same choices as 7-Zip's own "Add to Archive"
  window, for people who know what they want:
    Archive format       7z, zip or tar
    Compression level    Store, Fastest, Fast, Normal, Maximum, Ultra
    Compression method   LZMA2, LZMA, PPMd, BZip2 (7z); Deflate, Deflate64,
                         BZip2, LZMA, PPMd (zip)
    Dictionary size, word size, solid block size, CPU threads, memory limit
    Split into volumes   name.001, name.002 and so on
    Password             AES-256, with the option to hide file names (7z)
    Self-extracting      a .exe that unpacks itself (7z)
    Parameters           extra 7-Zip switches, typed by hand
  The lossless check runs for every one of these. Passwords are never saved.
  The -sdel switch (delete files after compressing) is refused, because
  Squish never touches your originals.
  "Use simple settings" in that window goes back to the two simple choices.

Light and dark mode
  The sun/moon button at the top right switches between the purple dark
  theme and the warm light theme. The choice is saved.

Portable and offline
  This folder runs on any Windows 10 or 11 PC and never uses the internet.
  It carries its own copy of 7-Zip's engine in the 7-Zip subfolder, so 7-Zip
  does not need to be installed, and its typeface is built into Squish.exe.
  Settings are kept in Squish.ini next to the app.

To extract an archive later, use 7-Zip (or Windows 11's built-in support).

Credits
  7-Zip is by Igor Pavlov and is free software; see 7-Zip\License.txt.
  The typeface is Jost by indestructible type*, used under the SIL Open Font
  License; see "Font license (Jost).txt".
