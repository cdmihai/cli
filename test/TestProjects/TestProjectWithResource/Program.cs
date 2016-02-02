using System;
using System.Resources;
using System.Reflection;

namespace ConsoleApplication
{
    public class Program
    {
        public static void Main(string[] args)
        {
            ResourceManager rm = new ResourceManager("Strings", typeof(Program).GetTypeInfo().Assembly);
      		string resourceString = rm.GetString("hello");

      		Console.WriteLine(resourceString);
        }
    }
}
