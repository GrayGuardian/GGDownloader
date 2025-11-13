using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace GGDownloader.Util
{
    public static class NetUtil
    {
        public static async Task<long> GetFileSize(string url)
        {
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "HEAD";

                using (HttpWebResponse response = (HttpWebResponse)await request.GetResponseAsync())
                {
                    return response.ContentLength;
                }
            }
            catch (Exception ex)
            {
            }
            return -1; // 获取失败
        }
    }
}
