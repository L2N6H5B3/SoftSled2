# SoftSled2
A revival of the old SoftSled Project, an open source Windows Media Center Extender

![Screenshot of the Dev Shell](https://raw.githubusercontent.com/L2N6H5B3/SoftSled2/master/Screenshot_230425.png)

## Current Progress
* Remote Rendering Video PIP / Zoom broken - not able to figure out where the instruction comes from to move / scale a video surface while in Remote Rendering mode.

## Finished Elements
* Full Pairing Configuration (Including on-the-fly Extender Certificate Generation)
* Device Services Remoting (DSLR)
* Extender Device Capability Queries (DSPA)
* Extender Device Session Communication (DSMN)
* Extender Device Media Control (DMCT)
* Interface Sounds
* RDP Bitmap OR Remote Rendering capabilities
* Audio RTSP Playback
   * MP3
   * PCM
* Video RTSP Playback
   * MPEG2
   * H.264

## Future Requirements
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
Work has been completed to enable on-the-fly certificate generation upon SoftSled provisioning / setup; however, the original certificates used with SoftSled do still work here (though password decryption will not be possible with these due to the lack of a Private Key) - these being:
* Linksys2200.cer
* Xbox360.cer

A patched version of Mcx2Prov.exe is required to enable pairing with the SoftSled-specific certificates, as these certificates do not contain a CRL.  This could be worked-around later on but the easiest option was to patch Mcx2Prov.exe to ignore the CRL check - for our use-cases, it doesn't matter and still allows all other extenders to pair and connect as normal.


### Configuration
(If using Linksys2200.cer)
1. Copy SoftSledCA.cer to the Host Machine and install the certificate into the Local Machine Trusted Root store.
2. Copy and replace Mcx2Prov.exe with the patched version - this patched version removes the CRL check which otherwise fails the Extender setup.
3. Start SoftSled > Setup.
4. Go to your Media Center PC and Navigate to **Settings** > **Extenders**.
5. Find SoftSled in the list.
6. Click Configure.
7. In the key type in the provided key, and WMC will try to pair the Extender.
8. Windows Media Center will go through the configuration steps and will pair the Extender.
9. When the Extender shows that it has received the user details, select Connect.
10. If you're lucky you'll get to the WMC home screen through RDP.
