<#
.SYNOPSIS
    HP Victus 15 Fan Control GUI
    Aplikasi antarmuka modern mandiri untuk mengontrol kipas laptop HP Victus 15 (FA1094TX / 15-fa series).
.DESCRIPTION
    Berkomunikasi langsung dengan Embedded Controller / WMI BIOS HP tanpa OMEN Gaming Hub yang berat.
    Dibuat untuk CODING TI UMY.
#>

# 1. Cek Hak Administrator (Wajib untuk akses WMI HP BIOS)
$currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($currentIdentity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    # Re-launch diri sendiri dengan hak Administrator
    $scriptPath = $MyInvocation.MyCommand.Path
    Start-Process powershell.exe -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$scriptPath`""
    exit
}

# 2. Muat Modul Core WMI
$coreScript = Join-Path $PSScriptRoot "VictusFanCore.ps1"
if (Test-Path $coreScript) {
    . $coreScript
} else {
    [System.Windows.MessageBox]::Show("File modul 'VictusFanCore.ps1' tidak ditemukan di folder yang sama!", "Error", "OK", "Error")
    exit
}

# 3. Muat Assembly GUI (WPF & XAML)
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Drawing

# 4. Desain XAML Tampilan Modern Dark Mode
[xml]$xaml = @"
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="HP Victus 15 Fan Control" Height="680" Width="520"
        WindowStartupLocation="CenterScreen" ResizeMode="CanMinimize"
        Background="#121316" FontFamily="Segoe UI" Foreground="#E0E0E0">
    <Window.Resources>
        <Style TargetType="Button">
            <Setter Property="FontWeight" Value="SemiBold"/>
            <Setter Property="FontSize" Value="13"/>
            <Setter Property="Cursor" Value="Hand"/>
            <Setter Property="Foreground" Value="#FFFFFF"/>
            <Setter Property="BorderThickness" Value="0"/>
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="Button">
                        <Border x:Name="border" Background="{TemplateBinding Background}" CornerRadius="8" Padding="{TemplateBinding Padding}">
                            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True">
                                <Setter TargetName="border" Property="Opacity" Value="0.85"/>
                            </Trigger>
                            <Trigger Property="IsPressed" Value="True">
                                <Setter TargetName="border" Property="Opacity" Value="0.7"/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>
    </Window.Resources>

    <Grid Margin="20">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/> <!-- Header -->
            <RowDefinition Height="Auto"/> <!-- Status Card -->
            <RowDefinition Height="Auto"/> <!-- Presets -->
            <RowDefinition Height="Auto"/> <!-- Manual Slider -->
            <RowDefinition Height="*"/>    <!-- Log / Activity -->
        </Grid.RowDefinitions>

        <!-- 1. Header -->
        <Border Grid.Row="0" Margin="0,0,0,16" Padding="12" Background="#1E2026" CornerRadius="10">
            <Grid>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="*"/>
                    <ColumnDefinition Width="Auto"/>
                </Grid.ColumnDefinitions>
                <StackPanel Grid.Column="0">
                    <TextBlock Text="❄️ HP VICTUS 15 FAN CONTROL" FontSize="18" FontWeight="Bold" Foreground="#00E5FF"/>
                    <TextBlock Text="Model: HP Victus 15-fa1xxx | WMI BIOS Interface" FontSize="11" Foreground="#888E9E" Margin="0,3,0,0"/>
                </StackPanel>
                <Border Grid.Column="1" Background="#13352A" CornerRadius="6" Padding="8,4" VerticalAlignment="Center">
                    <TextBlock Text="● WMI CONNECTED" FontSize="10" FontWeight="Bold" Foreground="#00E676"/>
                </Border>
            </Grid>
        </Border>

        <!-- 2. Status Card -->
        <Border Grid.Row="1" Margin="0,0,0,16" Padding="16" Background="#1A1C23" CornerRadius="12" BorderBrush="#2A2D37" BorderThickness="1">
            <Grid>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="*"/>
                    <ColumnDefinition Width="Auto"/>
                </Grid.ColumnDefinitions>
                <StackPanel Grid.Column="0">
                    <TextBlock Text="STATUS KIPAS SAAT INI" FontSize="11" FontWeight="Bold" Foreground="#7E8597"/>
                    <TextBlock x:Name="txtCurrentMode" Text="AUTO (BIOS DEFAULT)" FontSize="18" FontWeight="Bold" Foreground="#FFFFFF" Margin="0,4,0,2"/>
                    <TextBlock x:Name="txtStatusDesc" Text="Kecepatan kipas diatur otomatis oleh suhu laptop" FontSize="11" Foreground="#9AA0B2"/>
                </StackPanel>
                <Border Grid.Column="1" Width="70" Height="70" Background="#121316" CornerRadius="35" BorderBrush="#00E5FF" BorderThickness="2" VerticalAlignment="Center">
                    <TextBlock x:Name="txtSpeedDisplay" Text="AUTO" FontSize="14" FontWeight="Bold" Foreground="#00E5FF" HorizontalAlignment="Center" VerticalAlignment="Center"/>
                </Border>
            </Grid>
        </Border>

        <!-- 3. Preset Cepat -->
        <StackPanel Grid.Row="2" Margin="0,0,0,16">
            <TextBlock Text="PRESET KECEPATAN CEPAT" FontSize="11" FontWeight="Bold" Foreground="#7E8597" Margin="0,0,0,8"/>
            <Grid>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="*"/>
                    <ColumnDefinition Width="10"/>
                    <ColumnDefinition Width="*"/>
                </Grid.ColumnDefinitions>
                <Grid.RowDefinitions>
                    <RowDefinition Height="42"/>
                    <RowDefinition Height="10"/>
                    <RowDefinition Height="42"/>
                    <RowDefinition Height="10"/>
                    <RowDefinition Height="48"/>
                </Grid.RowDefinitions>

                <Button x:Name="btnAuto" Grid.Row="0" Grid.Column="0" Content="❄️ Auto (Default)" Background="#2D323F"/>
                <Button x:Name="btnQuiet" Grid.Row="0" Grid.Column="2" Content="🌙 Quiet (~2.500 RPM)" Background="#263445"/>

                <Button x:Name="btnBalanced" Grid.Row="2" Grid.Column="0" Content="⚡ Balanced (60%)" Background="#2B3A42"/>
                <Button x:Name="btnPerformance" Grid.Row="2" Grid.Column="2" Content="🔥 Heavy (85%)" Background="#4A3423"/>

                <!-- MAX TURBO BUTTON -->
                <Button x:Name="btnMaxFan" Grid.Row="4" Grid.ColumnSpan="3" Content="🚀 MAX FAN (100% TURBO)" 
                        Background="#D32F2F" FontSize="14" FontWeight="Bold"/>
            </Grid>
        </StackPanel>

        <!-- 4. Kontrol Slider Manual -->
        <Border Grid.Row="3" Margin="0,0,0,16" Padding="14" Background="#1A1C23" CornerRadius="10">
            <StackPanel>
                <Grid Margin="0,0,0,8">
                    <TextBlock Text="CUSTOM SPEED SLIDER" FontSize="11" FontWeight="Bold" Foreground="#7E8597" HorizontalAlignment="Left"/>
                    <TextBlock x:Name="txtSliderValue" Text="70%" FontSize="12" FontWeight="Bold" Foreground="#00E5FF" HorizontalAlignment="Right"/>
                </Grid>
                <Slider x:Name="sliderFan" Minimum="20" Maximum="100" Value="70" TickFrequency="5" IsSnapToTickEnabled="True" Margin="0,0,0,10"/>
                <Button x:Name="btnApplySlider" Content="Terapkan Kecepatan Custom" Height="36" Background="#00838F"/>
            </StackPanel>
        </Border>

        <!-- 5. Log Box -->
        <Border Grid.Row="4" Margin="0,0,0,10" Background="#0F1013" CornerRadius="8" Padding="10" BorderBrush="#252730" BorderThickness="1">
            <ScrollViewer x:Name="scrollLog" VerticalScrollBarVisibility="Auto">
                <TextBox x:Name="txtLog" Background="Transparent" Foreground="#76FF03" BorderThickness="0" 
                         FontFamily="Consolas" FontSize="11" IsReadOnly="True" TextWrapping="Wrap"/>
            </ScrollViewer>
        </Border>

    </Grid>
</Window>
"@

# 5. Render XAML Reader
$reader = (New-Object System.Xml.XmlNodeReader $xaml)
$window = [System.Windows.Markup.XamlReader]::Load($reader)

# Ambil Elemen GUI
$txtCurrentMode = $window.FindName("txtCurrentMode")
$txtStatusDesc  = $window.FindName("txtStatusDesc")
$txtSpeedDisplay= $window.FindName("txtSpeedDisplay")
$txtSliderValue = $window.FindName("txtSliderValue")
$sliderFan      = $window.FindName("sliderFan")
$btnAuto        = $window.FindName("btnAuto")
$btnQuiet       = $window.FindName("btnQuiet")
$btnBalanced    = $window.FindName("btnBalanced")
$btnPerformance = $window.FindName("btnPerformance")
$btnMaxFan      = $window.FindName("btnMaxFan")
$btnApplySlider = $window.FindName("btnApplySlider")
$txtLog         = $window.FindName("txtLog")
$scrollLog      = $window.FindName("scrollLog")

# Helper Penulis Log
function Add-Log {
    param ([string]$msg)
    $time = Get-Date -Format "HH:mm:ss"
    $line = "[$time] $msg`r`n"
    $txtLog.AppendText($line)
    $scrollLog.ScrollToEnd()
}

# Status Inisialisasi
Add-Log "Aplikasi FanControl berhasil dimuat."
Add-Log "Host: HP Victus 15-fa1xxx | WMI root\wmi:hpqBIntM siap."
Add-Log "Silakan pilih salah satu preset atau gunakan slider manual."

# Event Slider Berubah
$sliderFan.Add_ValueChanged({
    $val = [int]$sliderFan.Value
    $txtSliderValue.Text = "$val%"
})

# Event Handler Tombol
$btnAuto.Add_Click({
    Add-Log "Menerapkan Mode AUTO (Default BIOS)..."
    $res = Set-VictusMaxFan -Enable $false
    Set-VictusFanProfile -ProfileName "Default" | Out-Null
    
    $txtCurrentMode.Text = "AUTO (BIOS DEFAULT)"
    $txtCurrentMode.Foreground = [System.Windows.Media.Brushes]::LightGreen
    $txtStatusDesc.Text = "Kecepatan kipas diatur otomatis oleh suhu internal laptop"
    $txtSpeedDisplay.Text = "AUTO"
    $txtSpeedDisplay.Foreground = [System.Windows.Media.Brushes]::LightGreen
    Add-Log "-> Sukses: Mode Auto aktif."
})

$btnQuiet.Add_Click({
    $targetLevel = 15
    Add-Log "Menerapkan Mode QUIET (~2.500 RPM)..."
    $res = Set-VictusFanLevel -LevelPercent $targetLevel
    
    $txtCurrentMode.Text = "QUIET / SENYAP (~2.500 RPM)"
    $txtCurrentMode.Foreground = [System.Windows.Media.Brushes]::DeepSkyBlue
    $txtStatusDesc.Text = "Target kecepatan kipas sekitar 2.500 RPM untuk tugas ringan"
    $txtSpeedDisplay.Text = "~2.500 RPM"
    $txtSpeedDisplay.Foreground = [System.Windows.Media.Brushes]::DeepSkyBlue
    Add-Log "-> $($res.Message)"
})

$btnBalanced.Add_Click({
    $targetLevel = 60
    Add-Log "Menerapkan Mode BALANCED ($targetLevel%)..."
    $res = Set-VictusFanLevel -LevelPercent $targetLevel
    
    $txtCurrentMode.Text = "BALANCED ($targetLevel%)"
    $txtCurrentMode.Foreground = [System.Windows.Media.Brushes]::LightSkyBlue
    $txtStatusDesc.Text = "Kipas stabil untuk coding, multitasking, dan browsing"
    $txtSpeedDisplay.Text = "$targetLevel%"
    $txtSpeedDisplay.Foreground = [System.Windows.Media.Brushes]::LightSkyBlue
    Add-Log "-> Sukses: Level diset ke $targetLevel%."
})

$btnPerformance.Add_Click({
    $targetLevel = 85
    Add-Log "Menerapkan Mode HEAVY ($targetLevel%)..."
    $res = Set-VictusFanLevel -LevelPercent $targetLevel
    
    $txtCurrentMode.Text = "HEAVY / PERFORMANCE ($targetLevel%)"
    $txtCurrentMode.Foreground = [System.Windows.Media.Brushes]::Orange
    $txtStatusDesc.Text = "Pendinginan tinggi untuk rendering atau kompilasi kode berat"
    $txtSpeedDisplay.Text = "$targetLevel%"
    $txtSpeedDisplay.Foreground = [System.Windows.Media.Brushes]::Orange
    Add-Log "-> Sukses: Level diset ke $targetLevel%."
})

$btnMaxFan.Add_Click({
    Add-Log "Mengaktifkan MAX FAN (100% Turbo)..."
    $res = Set-VictusMaxFan -Enable $true
    
    $txtCurrentMode.Text = "MAX FAN (100% TURBO)"
    $txtCurrentMode.Foreground = [System.Windows.Media.Brushes]::OrangeRed
    $txtStatusDesc.Text = "Kipas berputar dengan kecepatan maksimum hardware"
    $txtSpeedDisplay.Text = "MAX"
    $txtSpeedDisplay.Foreground = [System.Windows.Media.Brushes]::OrangeRed
    Add-Log "-> Sukses: MAX FAN AKTIF!"
})

$btnApplySlider.Add_Click({
    $val = [int]$sliderFan.Value
    Add-Log "Menerapkan Kecepatan Kustom $val%..."
    $res = Set-VictusFanLevel -LevelPercent $val
    
    $txtCurrentMode.Text = "CUSTOM SPEED ($val%)"
    $txtCurrentMode.Foreground = [System.Windows.Media.Brushes]::Cyan
    $txtStatusDesc.Text = "Kipas diset manual ke target $val%"
    $txtSpeedDisplay.Text = "$val%"
    $txtSpeedDisplay.Foreground = [System.Windows.Media.Brushes]::Cyan
    Add-Log "-> Sukses: Level diset ke $val%."
})

# 6. Tampilkan Window
$window.ShowDialog() | Out-Null
