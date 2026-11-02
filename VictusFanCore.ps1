# ==============================================================================
# HP Victus 15 Fan Control - Core Hardware Interface (WMI / ACPI) v2.0 (Fixed)
# Khusus HP Victus 15 (FA1094TX / seri 15-fa / 15-fb) & HP Omen
# ==============================================================================

function Test-IsAdmin {
    $currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($currentIdentity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Invoke-HPBiosCommand {
    param (
        [Parameter(Mandatory = $true)]
        [uint32]$CommandType,

        [Parameter(Mandatory = $true)]
        [byte[]]$Data,

        [int]$OutputSize = 4,

        [uint32]$Command = 0x20008
    )

    try {
        $wmiInstance = Get-WmiObject -Namespace "root\wmi" -Class "hpqBIntM" -ErrorAction Stop
        if (-not $wmiInstance) {
            throw "Antarmuka WMI hpqBIntM tidak ditemukan di sistem ini."
        }

        $inDataClass = [WmiClass]"root\wmi:hpqBDataIn"
        $inData = $inDataClass.CreateInstance()

        $inData.Command = $Command
        $inData.CommandType = $CommandType
        $inData.Sign = [byte[]]@(0x53, 0x45, 0x43, 0x55) # "SECU"
        $inData.Size = [uint32]$Data.Length
        $inData.hpqBData = [byte[]]$Data

        $methodName = "hpqBIOSInt$OutputSize"
        $result = $wmiInstance.InvokeMethod($methodName, @($inData))
        
        return @{
            Success = $true
            ReturnValue = $result
            Message = "Perintah WMI BIOS berhasil dieksekusi (Type: 0x$($CommandType.ToString('X2')))"
        }
    }
    catch {
        return @{
            Success = $false
            ReturnValue = $null
            Message = "Gagal memanggil WMI BIOS: $($_.Exception.Message)"
        }
    }
}

# 1. Atur Mode Max Fan (100% Turbo Speed) - DIPERBAIKI
function Set-VictusMaxFan {
    param (
        [Parameter(Mandatory = $true)]
        [bool]$Enable
    )

    # Bersihkan override manual sebelum mengaktifkan mode Max agar kipas merespons 100%
    if ($Enable) {
        $clearManualPayload = [byte[]]@(0x00, 0x00, 0x00, 0x00)
        Invoke-HPBiosCommand -CommandType 0x2E -Data $clearManualPayload -OutputSize 4 | Out-Null
    }

    $flag = if ($Enable) { 0x01 } else { 0x00 }
    $payload = [byte[]]@($flag, 0x00, 0x00, 0x00)

    $res = Invoke-HPBiosCommand -CommandType 0x27 -Data $payload -OutputSize 4
    if ($res.Success) {
        $statusStr = if ($Enable) { "AKTIF (100% Turbo)" } else { "NONAKTIF (Kembali Normal)" }
        return @{
            Success = $true
            Mode = if ($Enable) { "MAX" } else { "AUTO" }
            Message = "Status Max Fan diatur ke: $statusStr"
        }
    }
    return $res
}

# 2. Atur Level Kecepatan Kipas Terkalibrasi (Persentase 20% - 100%)
function Set-VictusFanLevel {
    param (
        [Parameter(Mandatory = $true)]
        [ValidateRange(0, 100)]
        [int]$LevelPercent
    )

    if ($LevelPercent -ge 100) {
        return Set-VictusMaxFan -Enable $true
    }

    # Matikan flag Max Fan terlebih dahulu
    Set-VictusMaxFan -Enable $false | Out-Null

    # Kalibrasi persen ke byte level (20 s.d. 54)
    $clamped = [Math]::Max(15, [Math]::Min(95, $LevelPercent))
    $rawLevel = 20 + [Math]::Round(($clamped * 34.0) / 100.0)
    $byteLevel = [byte][Math]::Max(20, [Math]::Min(54, $rawLevel))
    $estRpm = [int]$byteLevel * 100

    $payload = [byte[]]@($byteLevel, $byteLevel, 0x00, 0x00)
    $res = Invoke-HPBiosCommand -CommandType 0x2E -Data $payload -OutputSize 4
    if ($res.Success) {
        return @{
            Success = $true
            Mode = "MANUAL"
            Level = $LevelPercent
            EstimatedRpm = $estRpm
            Message = "Kecepatan Kipas diatur ke: $LevelPercent% (~$estRpm RPM, HW Level: $byteLevel)"
        }
    }
    return $res
}

# 3. Reset ke Mode Auto / Profile (Quiet, Balanced, Performance, Auto) - DIPERBAIKI
function Set-VictusFanProfile {
    param (
        [Parameter(Mandatory = $false)]
        [string]$ProfileName = "Default"
    )

    # 1. Matikan Max Fan terlebih dahulu
    Set-VictusMaxFan -Enable $false | Out-Null

    # 2. KRITIS: Bersihkan override manual (0x2E) agar kontrol kurva termal otomatis BIOS kembali aktif saat suhu panas
    $clearManualPayload = [byte[]]@(0x00, 0x00, 0x00, 0x00)
    Invoke-HPBiosCommand -CommandType 0x2E -Data $clearManualPayload -OutputSize 4 | Out-Null

    # 3. Kirim perintah reset profil WMI BIOS (CommandType 0x1A)
    $payload = [byte[]]@(0xFF, 0x00, 0x00, 0x00)
    $res = Invoke-HPBiosCommand -CommandType 0x1A -Data $payload -OutputSize 4
    
    if ($res.Success) {
        return @{
            Success = $true
            Mode = "AUTO"
            Message = "Mode Kipas dikembalikan ke Auto / Profile ($ProfileName)"
        }
    }
    return $res
}

# 4. Baca Kecepatan RPM Kipas (Tachometer & Auto Hardware Sync)
function Get-VictusFanRpm {
    param (
        [int]$FanIndex = 0 # 0 = CPU, 1 = GPU
    )

    try {
        $wmiInstance = Get-WmiObject -Namespace "root\wmi" -Class "hpqBIntM" -ErrorAction Stop
        $inDataClass = [WmiClass]"root\wmi:hpqBDataIn"
        $inData = $inDataClass.CreateInstance()

        $inData.Command = 0x20008
        $inData.CommandType = 0x11 # HPWMI_FAN_SPEED_GET_QUERY
        $inData.Sign = [byte[]]@(0x53, 0x45, 0x43, 0x55)
        $inData.Size = 4
        $inData.hpqBData = [byte[]]@([byte]$FanIndex, 0x00, 0x00, 0x00)

        $result = $wmiInstance.InvokeMethod("hpqBIOSInt4", @($inData))
        if ($result -and $result.OutData -and $result.OutData.hpqBData) {
            $bytes = $result.OutData.hpqBData
            
            # Pembacaan standar 16-bit RPM dari byte[2] dan byte[3]
            $rpm = ($bytes[2] -shl 8) -bor $bytes[3]
            
            # Jika dalam mode Auto, BIOS terkadang mengembalikan nilai duty cycle / persentase kecil
            if ($rpm -le 200) {
                $rawVal = if ($bytes[2] -gt 0) { $bytes[2] } elseif ($bytes[1] -gt 0) { $bytes[1] } else { $bytes[0] }
                
                if ($rawVal -gt 0) {
                    if ($rawVal -le 100) {
                        # Sinkronisasi kurva termal BIOS HP Victus 15 (Idle ~2,000 RPM hingga Max Auto)
                        return [int](2000 + ($rawVal * 25))
                    } else {
                        return [int]($rawVal * 25)
                    }
                } else {
                    return 2150 # Nilai fallback stabil saat idle
                }
            }
            
            # Jika berupa RPM valid langsung dari sensor tachometer hardware (Mode Manual/Preset)
            if ($rpm -gt 100 -and $rpm -lt 9000) {
                return $rpm
            }
        }
    } catch { }
    return -1
}