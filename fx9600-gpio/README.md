# FX9600 GPIO 控制程式

Windows 主控台程式，用 Zebra FX Series Host .NET SDK 控制 FX9600 的 GPIO，做一個「感應器觸發讀取、燈號顯示結果」的 RFID 讀取點（例如輸送帶或門口）。

接線依照 **FX9600 Integration Guide Figure 139**（外部電源供電）：

| FX9600 腳位 | 訊號 | 接到 | 程式設定（預設） |
|---|---|---|---|
| 3 | GP O/P #1 | 紅燈 − | `RedGpo=1` |
| 4 | GP O/P #2 | 綠燈 − | `GreenGpo=2` |
| 5 | GP O/P #3 | 藍燈 − | `BlueGpo=3` |
| 8 | GP I/P #1 | Sensor #1 開關接點 | `StartSensorGpi=1` |
| 9 | GP I/P #2 | Sensor #2 開關接點 | `StopSensorGpi=2` |
| 7 | GPIO GND | 外部電源 − | — |
| 12 | GPIO GND | 感應器 GND 及開關另一端 | — |
| 1 | +24V DC | 不用（燈和感應器由外部電源 5–24 VDC 供電） | — |

燈的「+」和感應器 PWR 接外部電源「+」。外部電源「−」必須接 GPIO GND（Pin 7），否則沒有共地，電路不會動作。

## 運作方式

```
待機 Ready（藍燈）
   │ Sensor #1 觸發
   ▼
讀取中 Reading（燈全滅；讀到 MinTags 個標籤後亮綠燈）
   │ Sensor #2 觸發 / 達 MaxReadMs 上限
   ▼
收尾 Stopping（再讀 StopDelayMs，接收遲到的標籤）
   ▼
結果 Result：綠燈 = OK（≥ MinTags 個標籤），紅燈 = 不足；保持 ResultHoldMs
   │ 時間到 → 回到待機；期間 Sensor #1 再觸發 → 立即開始下一輪
   ▼
斷線 / 讀寫器錯誤 → Fault（紅 + 藍），每 ReconnectIntervalMs 自動重連，成功後回到待機
```

| 狀態 | 紅 | 綠 | 藍 |
|---|---|---|---|
| 待機 | | | ● |
| 讀取中（未夠標籤） | | | |
| 讀取中（已夠標籤）/ 結果 OK | | ● | |
| 結果不足 | ● | | |
| 故障 | ● | | ● |

注意：
- 只有 Sensor #1 的「觸發邊沿」會開始讀取。程式啟動時若 Sensor #1 已經處於觸發狀態，要等它放開再觸發一次。
- 如果只有一個感應器，把 `StopSensorGpi` 設為 `0`，Sensor #1 放開時就結束讀取。
- 網絡斷線期間電腦無法改變燈號，燈會停在斷線前的狀態，直至重連。
- 每輪結果寫入 `logs\cycles-yyyyMMdd.csv`（相對 .exe 所在資料夾），欄位：`cycle,start,end,duration_ms,outcome,end_reason,tag_count,tags`。

## 需要

- Windows 10/11 x64，.NET Framework 4.8
- Zebra FX Series Host .NET SDK 的 `Symbol.RFID3.Host.dll` 和 `Driver\64bits\RFIDAPI32PC.dll`，放入 [`lib/`](lib/README.md)
- 建置：Visual Studio 2022，或 .NET SDK 8（`dotnet build`）
- 電腦能連到 FX9600 的 LLRP 端口（預設 5084）

## 建置

```bash
cd fx9600-gpio
dotnet build -c Release
```

輸出在 `src\Fx9600Gpio\bin\Release\net48\`。整個資料夾複製到現場電腦即可，裏面有 `Fx9600Gpio.exe`、`Fx9600Gpio.exe.config` 和兩個 Zebra DLL。

單元測試（狀態機和設定，不需要讀寫器，任何作業系統都可以跑）：

```bash
dotnet test
```

## 第一次使用：先跑接線測試

FX9600 GPO 的邏輯極性（`PortState.TRUE` 是亮燈還是滅燈），以及 GPI 在感應器觸發時讀到 HIGH 還是 LOW，在 SDK 文件中都沒有寫明，所以要先實測：

```bash
Fx9600Gpio.exe --host 192.168.1.100 --test
```

1. **燈**：程式逐一把紅、綠、藍燈的 GPO 設為 `TRUE`，問你那盞燈有沒有亮。最後會建議 `LampOnState` 的值。
2. **感應器**：程式顯示每個 GPI 現在的電平，你逐一觸發感應器，畫面會即時顯示 `GPI n -> LOW/HIGH`。觸發時顯示 LOW 就設 `SensorActiveLow=true`，顯示 HIGH 就設 `false`。

把結果填入 `Fx9600Gpio.exe.config`。

## 正式執行

```bash
Fx9600Gpio.exe
```

`--host <IP>` 可以臨時覆蓋設定檔的 `ReaderHost`。按 Ctrl+C 停止：程式會停止讀取、關燈和斷線。

## 設定（`Fx9600Gpio.exe.config` → `appSettings`）

| Key | 預設 | 說明 |
|---|---|---|
| `ReaderHost` | `192.168.1.100` | FX9600 IP |
| `ReaderPort` / `ReaderTimeoutMs` | `0` / `0` | 0 = SDK 預設（5084 / 5000 ms） |
| `ReconnectIntervalMs` | `5000` | 斷線後重連間隔 |
| `RedGpo` / `GreenGpo` / `BlueGpo` | `1` / `2` / `3` | 燈接的 GPO 編號（1–4，不可重複） |
| `LampOnState` | `true` | 令燈亮的 GPO PortState，以 `--test` 結果為準 |
| `StartSensorGpi` | `1` | 開始讀取的感應器 |
| `StopSensorGpi` | `2` | 結束讀取的感應器；`0` = 不用，Sensor #1 放開即結束 |
| `SensorActiveLow` | `true` | 感應器觸發時 GPI 為 LOW（Figure 139 接法） |
| `MaxReadMs` | `10000` | 每輪最長讀取時間（硬上限） |
| `StopDelayMs` | `300` | 結束條件出現後多讀的時間 |
| `MinTags` | `1` | OK 需要的不重複標籤數 |
| `ResultHoldMs` | `2000` | 結果燈保持時間 |
| `GpiDebounceMs` | `0` | GPI 防彈跳（ms）；0 = 不改讀寫器設定 |
| `RmUser` / `RmPassword` | `admin` / 空 | 設定防彈跳用的讀寫器網頁管理帳戶；密碼空白則跳過 |
| `CycleLogDir` | `logs` | CSV 紀錄資料夾；空白 = 不寫 |

程式連線時會用讀寫器回報的 GPI/GPO 數量檢查這些編號。

## 程式結構

```
fx9600-gpio/
  src/Fx9600Gpio.Core/      netstandard2.0，不依賴 Zebra SDK
    PortalOptions.cs        設定讀取與檢查
    PortalStateMachine.cs   讀取點狀態機（待機 / 讀取 / 收尾 / 結果 / 故障）
    PortalTypes.cs          燈號、結果、IPortalHardware 介面
  src/Fx9600Gpio/           net48 x64 主控台程式
    FxReader.cs             Zebra SDK 封裝：連線、GPI/GPO、盤點、事件
    Program.cs              主迴圈：SDK 事件排入單一工作執行緒處理
    WiringTest.cs           --test 接線測試
    CycleLog.cs             每輪結果 CSV
    App.config              設定
  tests/Fx9600Gpio.Core.Tests/  xUnit 測試（28 個）
  lib/                      放 Zebra DLL（不入 Git）
```

所有讀寫器呼叫和狀態變更都在同一條工作執行緒上執行。SDK 事件（GPI、標籤、斷線）只會被排入佇列。唯一例外是 `GetReadTags`，按 SDK 文件的寫法在 `ReadNotify` 事件內呼叫。

## 尚未核實（請以 FX9600 Integration Guide 為準）

- GPO 輸出極性 → 用 `--test` 實測。
- GPI 是否有內部上拉、觸發時讀 HIGH 還是 LOW → 用 `--test` 實測。
- 每路 GPO 的最大吸入電流和耐壓、外部電源上限 → 選燈之前必須查規格。燈的電流超出 GPO 額定值時，要加繼電器。
- 本程式已在 Linux 上對 SDK 的 `Symbol.RFID3.Host.dll` 編譯通過，狀態機有單元測試，但**尚未連接實機測試**。
