using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;

namespace MSMQToAzureServiceBusFrame.Configuration
{
    class AppEncoding
    {

        public static byte[] DetectEncoding(String messageBody)
        {
            byte[] bytes = null;
            string encoding = GetXmlEncoding(messageBody);

            if ("UTF-16" == encoding)
            {
                bytes = Encoding.Unicode.GetBytes(messageBody);
            }
            else
            {
                bytes = Encoding.UTF8.GetBytes(messageBody);
            }

            return bytes;
        }

        static string GetXmlEncoding(string xmlDeclaration)
        {
            Regex regex = new Regex(@"encoding=""([^""]+)""", RegexOptions.IgnoreCase);
            Match match = regex.Match(xmlDeclaration);

            if (match.Success)
            {
                return match.Groups[1].Value;
            }
            return "Encoding not specified";
        }

    }
}
