# SoftSled2
A revival of the old SoftSled Project, an open source Windows Media Center Extender

![Screenshot of the Dev Shell](https://raw.githubusercontent.com/L2N6H5B3/SoftSled2/master/Screenshot_230425.png)

## Current Progress
* Audio RTSP communication needs work with ffplay and parsing RTSP / RTP packets
* Video RTSP communication needs work with ffplay and parsing RTSP / RTP packets
   * H.264 working
   * MPEG2-TS underway
   * Recorded TV and Live TV playback over H.264 is working!

## Finished Elements
* Full Pairing Configuration (Including on-the-fly Extender Certificate Generation)
* Device Services Remoting (DSLR)
* Extender Device Capability Queries (DSPA)
* Extender Device Session Communication (DSMN)
* Extender Device Media Control (DMCT)
* Interface Sounds
* Full-Screen Interface

## Future Requirements
* Implement Video Overlay (Not possible to Chroma-key the RDP window in WinForms or WPF... Need to think of another option.)
* Implement media controls (play/pause/previous/next) media from client
* Create RC6 remote control forwarder


## Possible Features to Add
* Create settings areas (perhaps could be used for dedicated device)
    * WLAN
    * Display
    * Audio

## Installation and configuration
### Prerequisites
* Windows 7 OR Windows 8 with Media Center (yes, SoftSled2 works with WMC8)

## Notes
### Certificates
Multiple certificates have been found to work with SoftSled - these being:
* Linksys2200.cer
* Xbox360.cer

Work has been completed to enable on-the-fly certificate generation upon SoftSled provisioning / setup.

### Configuration
(If using Linksys2200.cer)
1. Copy SoftSledCA.cer to the Host Machine and install the certificate into the Local Machine Trusted Root store.
2. Copy and replace Mcx2Prov.exe with the patched version.
3. Start SoftSled > Setup.
4. Go to your Media Center PC and Navigate to **Settings** > **Extenders**.
5. Find SoftSled in the list.
6. Click Configure.
7. In the key type in the provided key, and WMC will try to pair the Extender.
8. Windows Media Center will go through the configuration steps and will pair the Extender.
9. When the Extender shows that it has received the user details, select Connect.
10. If you're lucky you'll get to the WMC home screen through RDP.
