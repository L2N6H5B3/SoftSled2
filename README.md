# SoftSled2
A revival of the old SoftSled Project, an open source Windows Media Center Extender

![Screenshot of the Dev Shell](https://raw.githubusercontent.com/L2N6H5B3/SoftSled2/master/Screenshot.jpg)

## Current Progress
* Audio RTSP communication is almost complete, working on a media player buffer and interface.
* Video RTSP communication is started, need to figure out the AV signalling for video & add media player interface.
* Working out RDP devcaps functionality, some new discoveries made..

## Future Requirements
* Create toolbar overlay (for mouse support)
* Create RC6 control forwarder (through avctrl perhaps?)
* Create full-screen interface
* Create settings areas (optional, would be used for dedicated device)
    * WLAN
    * Display
    * Audio

## Installation and configuration
1. Start SoftSled
2. Go to your Media Center PC and Navigate to **Settings** > **Extenders**
3. Find SoftSled in the list there and take note of the key (It should be in the form ****-370*)
4. Click Configure
5. In the key type in 1234-3706, and WMC will try to pair the Extender. The certificates are a problem at this stage, and it seems the only way to get a successful pairing is to use the Linksys2200.cer certificate and change the password manually (netplwiz) on the host machine and SoftSled config after a successful certificate exchange.  
6. Once done, try to connect SoftSled, and if you're lucky you'll get to the WMC home screen through RDP.
