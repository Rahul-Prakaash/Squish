Game Compressor
===============

Turns game folders into lossless .7z archives. Works completely offline.

Use
  1. Run GameCompressor.exe (no install needed).
  2. Drag game folders onto the window, or use Add Folder.
  3. Check the predicted size, pick a compression level, press Start.

Each folder becomes its own .7z next to the original (or in a folder you
choose). Your original files are never changed or deleted.

Lossless, at every level
  Every compression level and dictionary size uses 7-Zip's lossless LZMA2,
  and every archive is proven before it is kept. This cannot be turned off:
    1. Compress.
    2. Fully decompress the archive and hash every file with SHA-256.
    3. Hash the original files the same way and compare: contents, names,
       file count, folder count and total size must all match.
  If anything differs, or some files could not be read (for example because
  the game is running), the archive is removed and the item is marked as
  failed. Double-click an item to see its verification report.

Managing archives
  Finished items have two buttons:
    Show    - opens the archive's folder with it selected.
    Delete  - sends the archive to the Recycle Bin (after asking). The
              original game files are not touched, and the item goes back
              into the queue so you can compress it again.

Size prediction
  Before you start, an evenly spread sample of each item is compressed with
  the same settings and the result is scaled up. Tested on real game data it
  was within about 5%. Folders of many similar small files (DLLs, engine
  files) usually end up smaller than predicted, never much bigger.

Portable
  This folder runs on any Windows 10 or 11 PC. It carries its own copy of
  7-Zip's engine in the 7-Zip subfolder, so 7-Zip does not need to be
  installed. Settings are kept in GameCompressor.ini next to the app.

To extract an archive later, use 7-Zip (or Windows 11's built-in .7z support).

7-Zip is by Igor Pavlov and is free software; see 7-Zip\License.txt.
