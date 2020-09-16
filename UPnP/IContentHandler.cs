using System;
using System.Collections.Generic;
using System.Text;
using System.Net;

namespace Intel.UPNP
{
    //MOD
	public interface IContentHandler
	{
        HTTPMessage HandleContent(string GetWhat, IPEndPoint local, HTTPMessage msg, HTTPSession WebSession);
	}
}
