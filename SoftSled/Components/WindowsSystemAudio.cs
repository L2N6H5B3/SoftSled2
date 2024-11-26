using System;
using System.Runtime.InteropServices;

namespace SystemAudio {
    internal static class WindowsSystemAudio {
        [ComImport]
        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
               InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator {
            void Vtbl();
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppDevice);
        }
        private static class MMDeviceEnumeratorFactory {
            public static IMMDeviceEnumerator CreateInstance() {
                return (IMMDeviceEnumerator)Activator.CreateInstance
                (Type.GetTypeFromCLSID(new Guid
                ("BCDE0395-E52F-467C-8E3D-C4579291692E")));
            }
        }
        [Guid("D666063F-1587-4E43-81F1-B948E807363F"),
               InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice {
            int Activate([MarshalAs(UnmanagedType.LPStruct)] Guid iid,
                          int dwClsCtx, IntPtr pActivationParams,
                          [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        }

        [Guid("5CDF2C82-841E-4546-9722-0CF74078229A"),
               InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioEndpointVolume {
            int RegisterControlChangeNotify(IntPtr pNotify);
            int UnregisterControlChangeNotify(IntPtr pNotify);
            int GetChannelCount(ref uint pnChannelCount);
            int SetMasterVolumeLevel(float fLevelDB, Guid pguidEventContext);
            int SetMasterVolumeLevelScalar(float fLevel, Guid pguidEventContext);
            int GetMasterVolumeLevel(ref float pfLevelDB);
            int GetMasterVolumeLevelScalar(ref float pfLevel);
            int SetMute(bool bMute, Guid pguidEventContext);
            int GetMute(ref bool pbMute);
        }

        internal static void SetVolume(int level) {
            try {
                IMMDeviceEnumerator deviceEnumerator =
                                    MMDeviceEnumeratorFactory.CreateInstance();
                IMMDevice speakers;
                const int eRender = 0;
                const int eMultimedia = 1;
                deviceEnumerator.GetDefaultAudioEndpoint
                                 (eRender, eMultimedia, out speakers);
                object aepv_obj;
                speakers.Activate(typeof(IAudioEndpointVolume).GUID,
                                  0, IntPtr.Zero, out aepv_obj);
                IAudioEndpointVolume aepv = (IAudioEndpointVolume)aepv_obj;
                Guid ZeroGuid = new Guid();
                int res = aepv.SetMasterVolumeLevelScalar(level / 100f, ZeroGuid);
            } catch (Exception ex) {
            }
        }

        internal static int GetVolume() {
            try {
                float currentLevel = 0f;
                IMMDeviceEnumerator deviceEnumerator =
                         MMDeviceEnumeratorFactory.CreateInstance();
                IMMDevice speakers;
                const int eRender = 0;
                const int eMultimedia = 1;
                deviceEnumerator.GetDefaultAudioEndpoint
                                 (eRender, eMultimedia, out speakers);
                object aepv_obj;
                speakers.Activate(typeof(IAudioEndpointVolume).GUID,
                                  0, IntPtr.Zero, out aepv_obj);
                IAudioEndpointVolume aepv = (IAudioEndpointVolume)aepv_obj;
                int res = aepv.GetMasterVolumeLevelScalar(ref currentLevel);
                return (int)(100 * currentLevel);  // Returned as an Integer 0 - 100
            } catch (Exception ex) {
                return -1;
            }
        }

        internal static void SetMute(bool mute) {
            try {
                IMMDeviceEnumerator deviceEnumerator =
                                    MMDeviceEnumeratorFactory.CreateInstance();
                IMMDevice speakers;
                const int eRender = 0;
                const int eMultimedia = 1;
                deviceEnumerator.GetDefaultAudioEndpoint
                                 (eRender, eMultimedia, out speakers);
                object aepv_obj;
                speakers.Activate(typeof(IAudioEndpointVolume).GUID,
                                  0, IntPtr.Zero, out aepv_obj);
                IAudioEndpointVolume aepv = (IAudioEndpointVolume)aepv_obj;
                Guid ZeroGuid = new Guid();
                int res = aepv.SetMute(mute, ZeroGuid);
            } catch (Exception ex) {
            }
        }

        internal static bool GetMute() {
            try {
                bool currentMute = false;
                IMMDeviceEnumerator deviceEnumerator =
                         MMDeviceEnumeratorFactory.CreateInstance();
                IMMDevice speakers;
                const int eRender = 0;
                const int eMultimedia = 1;
                deviceEnumerator.GetDefaultAudioEndpoint
                                 (eRender, eMultimedia, out speakers);
                object aepv_obj;
                speakers.Activate(typeof(IAudioEndpointVolume).GUID,
                                  0, IntPtr.Zero, out aepv_obj);
                IAudioEndpointVolume aepv = (IAudioEndpointVolume)aepv_obj;
                int res = aepv.GetMute(ref currentMute);
                return currentMute;
            } catch (Exception ex) {
                return false;
            }
        }
    }
}