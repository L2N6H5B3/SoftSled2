using SoftSled.Components.RTSP;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SoftSledWPF.Components.AudioVisual {
    class PlaybackHandler {

        private RTSPClient rtspClient;

        public bool OpenMedia(string mediaUrl) {

            //rtspClient = new RTSPClient();
            //rtspClient.Connect(DMCTOpenMediaURL, RTSPClient.RTP_TRANSPORT.UDP, RTSPClient.MEDIA_REQUEST.VIDEO_AND_AUDIO);

            return true;
        }

        public bool PauseMedia(string mediaUrl) {


            return true;
        }
    }
}
