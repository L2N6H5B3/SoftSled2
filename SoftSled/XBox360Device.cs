using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Intel.UPNP;
using SoftSled;
using SoftSled.Components;

namespace SoftSled
{
    class XBox360Device
    {
        private UPnPDevice device;
        private Logger m_logger;
        private ContentHandler rootContentHandler;
        private ContentHandler mcxContentHandler;

        public XBox360Device(Logger logger)
        {
            if (logger == null)
                throw new ArgumentNullException("logger");

            m_logger = logger;

            device = UPnPDevice.CreateRootDevice(1800, 1.0, "\\");
            device.UniqueDeviceName = "10000000-0000-0000-0200-00125AB07AEF";
            device.FriendlyName = "Xbox 360 Media Center Extender";
            device.Manufacturer = "Microsoft Corporation";
            device.ManufacturerURL = "http://www.xbox.com/";
            device.ModelName = "Xbox 360";
            device.ModelDescription = "Xbox 360 Media Center Extender";
            device.ModelNumber = "";
            device.ModelURL = new Uri("http://go.microsoft.com/fwlink/?LinkID=53081");
            device.HasPresentation = false;
            device.DeviceURN = "urn:schemas-microsoft-com:device:MediaCenterExtenderMFD:1";
            device.AddCustomFieldInDescription("X_deviceCategory", "MediaDevices", "http://schemas.microsoft.com/windows/pnpx/2005/11");
            device.ProductCode = "";
            device.SerialNumber = "";

            rootContentHandler = new ContentHandler(m_logger);
            device.ContentHandler = rootContentHandler;

            SoftSled.NullService NullService = new SoftSled.NullService();
            device.AddService(NullService);

            UPnPDevice device1 = UPnPDevice.CreateEmbeddedDevice(1, Guid.NewGuid().ToString());
            device1.FriendlyName = "Xbox 360 Media Center Extender";
            device1.Manufacturer = "Microsoft Corporation";
            device1.ManufacturerURL = "http://www.microsoft.com/";
            device1.ModelName = "Xbox 360";
            device1.ModelDescription = "Xbox 360 Media Center Extender";
            device1.ModelNumber = "";
            device1.ModelURL = new Uri("http://go.microsoft.com/fwlink/?LinkID=53081");
            device1.HasPresentation = false;
            device1.SerialNumber = "";
            device1.ProductCode = "";
            device1.DeviceURN = "urn:schemas-microsoft-com:device:MediaCenterExtender:1";
            device1.AddCustomFieldInDescription("X_compatibleId", "MICROSOFT_MCX_0001", "http://schemas.microsoft.com/windows/pnpx/2005/11");
            device1.AddCustomFieldInDescription("X_deviceCategory", "MediaDevices", "http://schemas.microsoft.com/windows/pnpx/2005/11");
            mcxContentHandler = new ContentHandler(m_logger);
            device1.ContentHandler = mcxContentHandler;


            SoftSled.TrustAgreementService TrustAgreementService = new SoftSled.TrustAgreementService(m_logger);
            TrustAgreementService.External_Commit = new SoftSled.TrustAgreementService.Delegate_Commit(TrustAgreementService.Commit);
            TrustAgreementService.External_Confirm = new SoftSled.TrustAgreementService.Delegate_Confirm(TrustAgreementService.Confirm);
            TrustAgreementService.External_Exchange = new SoftSled.TrustAgreementService.Delegate_Exchange(TrustAgreementService.Exchange);
            TrustAgreementService.External_Validate = new SoftSled.TrustAgreementService.Delegate_Validate(TrustAgreementService.Validate);
            device1.AddService(TrustAgreementService);

            SoftSled.RemotedExperienceService RemotedExperienceService = new SoftSled.RemotedExperienceService(m_logger);
            RemotedExperienceService.External_AcquireNonce = new SoftSled.RemotedExperienceService.Delegate_AcquireNonce(RemotedExperienceService.AcquireNonce);
            RemotedExperienceService.External_Advertise = new SoftSled.RemotedExperienceService.Delegate_Advertise(RemotedExperienceService.Advertise);
            RemotedExperienceService.External_Inhibit = new SoftSled.RemotedExperienceService.Delegate_Inhibit(RemotedExperienceService.Inhibit);
            device1.AddService(RemotedExperienceService);


            device.AddDevice(device1);

        }

        public void Start()
        {
            device.StartDevice(3391);

            m_logger.LogInfo("Started Device Broadcasting");
        }
        public void Stop()
        {
            device.StopDevice();

            m_logger.LogInfo("Stopped Device Broadcasting");
        }

    
    }
}
