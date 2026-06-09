using System;
using System.Collections.Generic;
using System.Text;

namespace RemoteDataSource
{
    public enum SourceLocation
    { 
        UpLocation,     // Узел верхнего уровня 
        DownLocation    // Узел нижнего уровня   
    }
    public interface RemoteDataSource
    {        
        Guid ID { get; }
        public SourceLocation Location { get; }
        public string Name { get; }
        public string Description { get; }
        public Bitmap Icon { get; }
        public string IpAddress { get; set; }
        public string HostName { get; set; }
        //PortSettings Settings { get; set; }
    }
}
