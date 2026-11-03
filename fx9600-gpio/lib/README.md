# Zebra SDK 檔案（不放入 Git）

把以下兩個檔案從 **Zebra RFID FXSeries Host .NET SDK** 複製到這個資料夾：

| 檔案 | SDK 內的位置 | 用途 |
|---|---|---|
| `Symbol.RFID3.Host.dll` | `SDK\Symbol.RFID3.Host.dll` | .NET API，編譯時需要 |
| `RFIDAPI32PC.dll` | `Driver\64bits\RFIDAPI32PC.dll` | 原生驅動（x64），執行時需要 |

這兩個檔案屬 Zebra 授權，`.gitignore` 已排除 `lib/*.dll`，請不要提交。
缺少 `Symbol.RFID3.Host.dll` 時建置會報錯；缺少 `RFIDAPI32PC.dll` 時建置只會警告，但程式無法執行。

本專案是用 SDK 內 2024-04-25 版本的 DLL 建置和測試的（`Symbol.RFID3.Host.dll` MD5 `a9f0abc7958297866fed9dea19a2a064`）。
