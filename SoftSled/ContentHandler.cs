using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Intel.UPNP;
using SoftSled.Components;

namespace SoftSled
{
    class ContentHandler : IContentHandler
    {
        private Logger m_logger;

        public ContentHandler(Logger logger)
        {
            if (logger == null)
                throw new ArgumentNullException("logger");

            m_logger = logger;
        }
        #region IContentHandler Members

        public HTTPMessage HandleContent(string GetWhat, System.Net.IPEndPoint local, HTTPMessage msg, HTTPSession WebSession)
        {

            m_logger.LogInfo("HandleContent GetWhat = '" + GetWhat + "'");
            HTTPMessage message = new HTTPMessage();
            message.StatusCode = 200;
            message.StatusData = "OK";
            string tagData = "text/xml";

            message.BodyBuffer = new System.Text.ASCIIEncoding().GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\"?><blah>" + GetWhat + "</blah>");
            message.AddTag("Content-Type", tagData);
            WebSession.Send(message);

            return null;
        }

        #endregion
    }
}
