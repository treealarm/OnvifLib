$client = New-Object System.Net.WebClient
$currentDir = (Get-Item -Path ".\" -Verbose).FullName + "\"
$wsdlsDir = $currentDir + "wsdl\"

New-Item -ItemType Directory -Force -Path $wsdlsDir | Out-Null

$wsdl_urls = @(
    @("https://www.onvif.org/ver10/device/wsdl/devicemgmt.wsdl", "ver10/device/wsdl/devicemgmt.wsdl"),
    @("https://www.onvif.org/ver10/events/wsdl/event.wsdl", "ver10/events/wsdl/event.wsdl"),
    @("https://www.onvif.org/ver10/pacs/accesscontrol.wsdl", "ver10/pacs/accesscontrol.wsdl"),
    @("https://www.onvif.org/ver10/accessrules/wsdl/accessrules.wsdl", "ver10/accessrules/wsdl/accessrules.wsdl"),
    @("https://www.onvif.org/ver10/actionengine.wsdl", "ver10/actionengine.wsdl"),
    @("https://www.onvif.org/ver20/analytics/wsdl/analytics.wsdl", "ver20/analytics/wsdl/analytics.wsdl"),
    @("https://www.onvif.org/ver10/appmgmt/wsdl/appmgmt.wsdl", "ver10/appmgmt/wsdl/appmgmt.wsdl"),
    @("https://www.onvif.org/ver10/authenticationbehavior/wsdl/authenticationbehavior.wsdl", "ver10/authenticationbehavior/wsdl/authenticationbehavior.wsdl"),
    @("https://www.onvif.org/ver10/credential/wsdl/credential.wsdl", "ver10/credential/wsdl/credential.wsdl"),
    @("https://www.onvif.org/ver10/deviceio.wsdl", "ver10/deviceio.wsdl"),
    @("https://www.onvif.org/ver10/display.wsdl", "ver10/display.wsdl"),
    @("https://www.onvif.org/ver10/pacs/doorcontrol.wsdl", "ver10/pacs/doorcontrol.wsdl"),
    @("https://www.onvif.org/ver20/imaging/wsdl/imaging.wsdl", "ver20/imaging/wsdl/imaging.wsdl"),
    @("https://www.onvif.org/ver10/media/wsdl/media.wsdl", "ver10/media/wsdl/media.wsdl"),
    @("https://www.onvif.org/ver20/media/wsdl/media.wsdl", "ver20/media/wsdl/media.wsdl"),
    @("https://www.onvif.org/ver10/provisioning/wsdl/provisioning.wsdl", "ver10/provisioning/wsdl/provisioning.wsdl"),
    @("https://www.onvif.org/ver20/ptz/wsdl/ptz.wsdl", "ver20/ptz/wsdl/ptz.wsdl"),
    @("https://www.onvif.org/ver10/receiver.wsdl", "ver10/receiver.wsdl"),
    @("https://www.onvif.org/ver10/recording.wsdl", "ver10/recording.wsdl"),
    @("https://www.onvif.org/ver10/search.wsdl", "ver10/search.wsdl"),
    @("https://www.onvif.org/ver10/replay.wsdl", "ver10/replay.wsdl"),
    @("https://www.onvif.org/ver10/schedule/wsdl/schedule.wsdl", "ver10/schedule/wsdl/schedule.wsdl"),
    @("https://www.onvif.org/ver10/advancedsecurity/wsdl/advancedsecurity.wsdl", "ver10/advancedsecurity/wsdl/advancedsecurity.wsdl"),
    @("https://www.onvif.org/ver10/thermal/wsdl/thermal.wsdl", "ver10/thermal/wsdl/thermal.wsdl"),
    @("https://www.onvif.org/ver10/uplink/wsdl/uplink.wsdl", "ver10/uplink/wsdl/uplink.wsdl"),
    @("https://www.onvif.org/ver10/schema/onvif.xsd", "ver10/schema/onvif.xsd"),
    @("https://www.onvif.org/ver10/schema/common.xsd", "ver10/schema/common.xsd"),
    @("https://www.onvif.org/ver10/schema/metadatastream.xsd", "ver10/schema/metadatastream.xsd"),
    @("https://www.onvif.org/ver10/pacs/types.xsd", "ver10/pacs/types.xsd"),
    @("https://www.onvif.org/ver20/analytics/rules.xsd", "ver20/analytics/rules.xsd"),
    @("https://www.onvif.org/ver20/analytics/humanbody.xsd", "ver20/analytics/humanbody.xsd"),
    @("https://www.onvif.org/ver20/analytics/humanface.xsd", "ver20/analytics/humanface.xsd")
)

foreach ($url in $wsdl_urls) {
    $targetPath = $wsdlsDir + $url[1]
    $targetDir = Split-Path -Parent $targetPath
    New-Item -ItemType Directory -Force -Path $targetDir | Out-Null
    Write-Host $("Downloading: " + $url[0])
    $client.DownloadFile($url[0], $targetPath)
}

Write-Host "All files downloaded successfully!"
