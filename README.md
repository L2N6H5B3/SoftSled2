# SoftSled2
A revival of the old SoftSled Project, an open source Windows Media Center Extender

![Screenshot of the Dev Shell](https://raw.githubusercontent.com/L2N6H5B3/SoftSled2/master/Screenshot.jpg)

## Current Progress
* Audio RTSP communication is close to complete, RTSP playback working via the AxWMP control.
* Video RTSP communication is mostly done, RTSP playback working via the AxWMP control for WMV Videos but not MP4, AVI, or WTV (Recorded TV).  Unsure why as some RTSP clients just state "Internal Server Error", perhaps because of codecs / Protocol String / DRM license failure?
* OnMediaEvent requests are working via the AxWMP control, though this will need to be revisited if the WMP control will not be used moving into the future.
* Interface sounds (through RDP or local) currently being worked out

## Finished Elements
* Initial Device Configuration
* Device Services Remoting (DSLR)
* Extender Device Capability Queries (DSPA)
* Extender Device Session Communication (DSMN)
* Extender Device Media Control (DMCT)
* Opening Sounds
* Full-Screen Interface

## Future Requirements
* Implement Video Overlay (Not possible to Chroma-key the RDP window in WinForms or WPF... Need to think of another option.)
* Create Extender Certificate
* Implement media controls (play/pause/previous/next) media from client
* Create RC6 remote control forwarder


## Possible Features to Add
* Create settings areas (perhaps could be used for dedicated device)
    * WLAN
    * Display
    * Audio

## Installation and configuration
### Prerequisites
* Windows 7

### Configuration
(If using Linksys2200.cer)
1. Copy Linksys2200.cer to the Host Machine and let Windows install the certificate into the default store.
2. Start SoftSled.
3. Go to your Media Center PC and Navigate to **Settings** > **Extenders**.
4. Find SoftSled in the list.
5. Click Configure.
6. In the key type in 1234-3706, and WMC will try to pair the Extender.
7. Windows Media Center will go through the configuration steps and will pair the Extender, but will fail the last step and won't be able to connect.
8. Open netplwiz, and note the new user (in the form of Mcx{num}-{machineName}). 
9. Manually reset the password of this user to **mcxpw123**.
10. Once done, try to connect SoftSled, and if you're lucky you'll get to the WMC home screen through RDP.
