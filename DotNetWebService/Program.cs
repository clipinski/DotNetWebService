﻿﻿////////////////////////////////////////////////////////////////////////////////////////
// MIT License
//
// Copyright (c) 2025 Craig J. Lipinski
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.
/////////////////////////////////////////////////////////////////////////////////////////

using System;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Reflection;
using System.Reflection.Metadata;
using System.Runtime.CompilerServices;

/////////////////////////////////////////////////////////////////////////////////////////
// Sample Rest API (DotNetWebService)
// 
// The intended purpose of this code is to teach the basic creation of a multi-threaded 
//   web service in .net code and C#.
/////////////////////////////////////////////////////////////////////////////////////////
class DotNetWebService
{
    // For the purposes of this demo application (in lieu of adding database suport)
    //  we will simply create a list of "Animal" objects that can be accessed and 
    //  manipulated by this API.
    class Animal {
        public required int Id { get; set; }
        public required string Species { get; set; }
        public required string Habitat { get; set; }
    }

    static List<Animal> _Animals = new List<Animal>()
    { 
        new Animal { Id = 1, Species = "Giraffe", Habitat = "Savannah" },
        new Animal { Id = 2, Species = "Walrus", Habitat = "Arctic" },
        new Animal { Id = 3, Species = "Monkey", Habitat = "Forest" },
        new Animal { Id = 4, Species = "Toucan", Habitat = "Rainforest" }
    };

    // We will use this for simple thread synchronization
    static object _AnimalsLock = new object();

    // This defines a custom attribute that we will use to add metadata to our
    //   request handler methods.  This will allow us to send incoming requests 
    //   to the correct handler based on the incoming URL.
    class Endpoint : Attribute
    {
        public string Action;
        public string Route;
        public Endpoint(string action, string route)
        {
            Action = action;
            Route = route;
        }
    }
        
    /////////////////////////////////////////////////////////////////////////////////////
    /// Get Animals Endpoint
    /////////////////////////////////////////////////////////////////////////////////////
    [Endpoint("GET", "animals")]
    static void HandleGetAnimals(HttpListenerContext context)
    {
        if (context?.Request?.Url is not null) 
        { 
            // Check for a 3rd segment to the URL.  This would indicate an ID
            // Example: localhost:8080/animals/1
            if (context?.Request?.Url?.Segments?.Length == 3)
            {
                // Get the requested animal ID from the URL
                int requestedId = 0;
                if (int.TryParse(context.Request.Url.Segments[2].Replace("/", ""), out requestedId))
                {
                    // Since we have setup processing of requests asynchronously, we must ensure
                    //  only one thread can read/write to our main list at a time
                    lock(_AnimalsLock)
                    {
                        // Find that animal in the list
                        Animal? theAnimal = _Animals.Where(animal => animal.Id == requestedId).FirstOrDefault();
                        if (theAnimal is not null) 
                        {
                            SendJSONResponse(context, theAnimal);
                        }
                        else
                        {
                            SendErrorResponse(context, (int)HttpStatusCode.NotFound, "Requested animal not found.");
                        }
                    }
                }
            }
            else
            {
                // No ID was given so just return the full list
                // Since we have setup processing of requests asynchronously, we must ensure
                //  only one thread can read/write to our main list at a time
                lock(_AnimalsLock)
                {
                    SendJSONResponse(context, _Animals);
                }
            }
        }
    }

    /////////////////////////////////////////////////////////////////////////////////////
    /// Method: ProcessRequest
    /// 
    /// This method will "process" incoming web requests by sending them to the
    ///   appropriate handler method.
    /////////////////////////////////////////////////////////////////////////////////////
    static void ProcessRequest(HttpListenerContext context)
    {
        try
        {
            if (context?.Request?.Url is not null)
            {
                // We are only handleing requests that have at least 2 segments
                //   the "root" segment ("/") and the route name (in this case something like "animals")
                if (context?.Request?.Url?.Segments?.Length > 1)
                {
                    // Get our route from the URL
                    String route = context.Request.Url.Segments[1].Replace("/", "");

                    // Get the HTTP method from the request
                    String action = context.Request.HttpMethod;

                    // Get the first method in this class that have HandleRoute attribute where the route is equal
                    //  to the URL segment from the incoming request
                    // NOTE: BindingFlags are used to make sure we find out private static methods
                    var method = typeof(DotNetWebService)
                                        .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
                                        .Where(method => method.GetCustomAttributes(true).Any(attr => attr is Endpoint && ((Endpoint)attr).Route == route && ((Endpoint)attr).Action == action))
                                        .FirstOrDefault();

                    // Call that method if we found it
                    if (method is not null)
                    {
                        method.Invoke(null, new object[]{context});
                    }
                    else
                    {
                        // Return 404
                        context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                        context.Response.OutputStream.Close();
                    }
                }
                else
                {
                    // Return 404
                    if (context?.Request is not null)
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                        context.Response.OutputStream.Close();               
                    }
                }
            }
        }
        catch (Exception e)
        {
            Console.Write($"\n\nUnhandled Exception! {e.Message}");

            if (context?.Response is not null)
            {
                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                context.Response.OutputStream.Close();          
            }
        }
    }

    /////////////////////////////////////////////////////////////////////////////////////
    /// Method: StartListening
    /// 
    /// Main loop for the service.  This will start the HTTPListener on the specified 
    ///   port, wait for incoming requests, and then make a call to process each request.
    /////////////////////////////////////////////////////////////////////////////////////
    static async Task StartListening(int port, int maxConcurrentListeners, CancellationToken ct)
    {
        // Create HTTP Listener
        HttpListener listener = new HttpListener();

        try
        {
            // Specifiy we will handle all incoming requests
            //  on our configured port
            listener.Prefixes.Add($"http://localhost:{port}/");

            // Begin Listening
            listener.Start();
            Console.Write($"\n\n-- Listening on Port: {port}");

            // Create and start (maxConcurrentListeners) number of tasks that will be listening
            //  for incoming requests.
            // We pass our cancellation token to "WaitAsync" so that
            //  then cancellation is requsted, if we are waiting for an incoming request, the
            //  TaskCanceledException will be thrown and we will stop waiting.
            //
            // NOTE: This is surely "overkill" for our simple server, but meant to serve
            //        as an example of one way to accomplish concurrency
            var concurrentListeners = new HashSet<Task>();
            for(int i = 0; i < maxConcurrentListeners; i++)
            {
                concurrentListeners.Add(listener.GetContextAsync().WaitAsync(ct));
            }

            // Keep going until we recieve a cancellation request
            while(!ct.IsCancellationRequested)
            {
                // Wait for any of our tasks to finish
                Task t = await Task.WhenAny(concurrentListeners);

                // Get the context from the completed task
                HttpListenerContext context = ((Task<HttpListenerContext>)t).Result;

                // Process the request in a new thread
                _ = Task.Run(() => ProcessRequest(context));

                // Remove the completed listener task from the list and start up a new one
                concurrentListeners.Remove(t);
                concurrentListeners.Add(listener.GetContextAsync().WaitAsync(ct));
            }

        }
        catch (TaskCanceledException)
        {
            Console.Write($"\n\n-- Shutdown Requested...");
        }
        catch (Exception e)
        {
            if (e.InnerException is TaskCanceledException)
            {
                Console.Write($"\n\n-- Shutdown Requested...");
            }
            else
            {
                Console.Write($"\n\n-- ERROR! {e.Message}");
            }
        }
        finally
        {
            // May stop listening now
            listener.Stop();

            Console.Write($"\n\n-- Shutting Down...");
        }
    }
    
    /////////////////////////////////////////////////////////////////////////////////////
    /// Method: Main
    /// 
    /// Main entry point for the service
    /////////////////////////////////////////////////////////////////////////////////////
    static void Main()
    {
        // Create a cancellation token source to manage a cancellation token for us.
        // This will be used to stop the service
        CancellationTokenSource cts = new CancellationTokenSource();
        CancellationToken token = cts.Token;

        // Start our listening in a new thread
        _ = Task.Run(() => StartListening(8080, 16, token), token);

        Console.Write("\nDotNetWebService is running!");
        Console.Write("\n\nPress any key to shutdown service...");
        Console.ReadKey(true);

        // Shutdown
        cts.Cancel();
        cts.Dispose();

        Console.Write("\n\nPress any key to exit...");
        Console.ReadKey(true);

        return;
    }

    /////////////////////////////////////////////////////////////////////////////////////
    /// HELPERS
    /////////////////////////////////////////////////////////////////////////////////////
    
    /////////////////////////////////////////////////////////////////////////////////////
    /// Method: SendJSONResponse
    /// 
    /// Returns an object to the caller of an API request as JSON.
    /////////////////////////////////////////////////////////////////////////////////////
    static void SendJSONResponse(HttpListenerContext? context, object obj)
    {
        if (context?.Response is not null) 
        {
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            var buffer = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(obj));
            context.Response.ContentLength64 = buffer.Length;
            context.Response.OutputStream.Write(buffer, 0, buffer.Length);
            context.Response.OutputStream.Close();
        }
    }

    /////////////////////////////////////////////////////////////////////////////////////
    /// Method: SendErrorResponse
    /// 
    /// Returns an error to the caller of the API request.
    /////////////////////////////////////////////////////////////////////////////////////
    static void SendErrorResponse(HttpListenerContext? context, int statusCode, String err)
    {
        if (context?.Response is not null) 
        {
            context.Response.StatusCode = statusCode;
            var buffer = Encoding.UTF8.GetBytes($"<HTML><BODY><span>{err}</span></BODY></HTML>");
            context.Response.ContentLength64 = buffer.Length;
            context.Response.OutputStream.Write(buffer, 0, buffer.Length);
            context.Response.OutputStream.Close();
        }
    }
}