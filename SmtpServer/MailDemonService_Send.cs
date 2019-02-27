﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using DnsClient;

using MailKit.Net.Smtp;

using MimeKit;

namespace MailDemon
{
    public partial class MailDemonService
    {
        /// <summary>
        /// Set to true to turn off actual sending of smtp messages, useful for performance testing
        /// </summary>
        public bool DisableSending { get; set; }

        // h=Subject:To:Cc:References:From:Message-ID:Date:MIME-Version:In-Reply-To:Content-Type
        private static readonly HeaderId[] headersToSign = new HeaderId[] { HeaderId.From, HeaderId.Cc, HeaderId.Subject, HeaderId.Date, HeaderId.MessageId, HeaderId.References,
            HeaderId.MimeVersion, HeaderId.InReplyTo, HeaderId.ContentType, HeaderId.ContentLanguage, HeaderId.Body };

        /// <summary>
        /// Send mail and kicks off the send in a new task
        /// </summary>
        /// <param name="foundUser">Found user</param>
        /// <param name="reader">Reader</param>
        /// <param name="writer">Writer</param>
        /// <param name="line">Line</param>
        /// <param name="endPoint">End point</param>
        /// <param name="validateSpf">Whether to validate spf</param>
        /// <param name="onPrepare">Called before message is prepared</param>
        /// <returns>Task</returns>
        private async Task SendMail(MailDemonUser foundUser, Stream reader, StreamWriter writer, string line, IPEndPoint endPoint, Action<MimeMessage> onPrepare = null)
        {
            MailFromResult result = await ParseMailFrom(foundUser, reader, writer, line, endPoint);
            SendMail(writer, result, endPoint, true, onPrepare).GetAwaiter();
            await writer.WriteLineAsync($"250 2.1.0 OK");
        }

        /// <summary>
        /// Send mail and awaits until all messages are sent
        /// </summary>
        /// <param name="writer">Writer</param>
        /// <param name="result">Mail result to send</param>
        /// <param name="endPoint">End point</param>
        /// <param name="dispose">Whether to dispose result when done</param>
        /// <param name="onPrepare">Called before message is prepared</param>
        /// <returns>Task</returns>
        private async Task SendMail(StreamWriter writer, MailFromResult result, IPEndPoint endPoint, bool dispose, Action<MimeMessage> onPrepare = null)
        {
            try
            {
                // send all emails in one shot for each domain in order to batch
                DateTime start = DateTime.UtcNow;
                int count = 0;
                List<Task> tasks = new List<Task>();
                foreach (var group in result.ToAddresses)
                {
                    tasks.Add(SendMailInternal(writer, result.BackingFile, result.From, group.Key, group.Value, endPoint, onPrepare));
                    count++;
                }
                await Task.WhenAll(tasks);
                MailDemonLog.Info("Sent {0} batches of messages in {1:0.00} seconds", count, (DateTime.UtcNow - start).TotalSeconds);
            }
            catch (Exception ex)
            {
                MailDemonLog.Error(ex);
            }
            finally
            {
                if (dispose)
                {
                    result.Dispose();
                }
            }
        }

        private async Task SendMailInternal(StreamWriter writer, string fileName, MailboxAddress from, string toDomain, IEnumerable<MailboxAddress> toAddresses,
            IPEndPoint endPoint, Action<MimeMessage> onPrepare)
        {
            try
            {
                using (SmtpClient client = new SmtpClient()
                {
                    ServerCertificateValidationCallback = (object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors) =>
                    {
                        return (sslPolicyErrors == SslPolicyErrors.None ||
                            (ignoreCertificateErrorsRegex.TryGetValue(toDomain, out Regex re) && re.IsMatch(certificate.Subject)));
                    }
                })
                {
                    using (Stream fs = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true))
                    {
                        MimeMessage message = await MimeMessage.LoadAsync(fs, true, cancelToken);
                        IPHostEntry ip = null;
                        LookupClient lookup = new LookupClient();
                        MailDemonLog.Debug("QueryAsync mx for domain {0}", toDomain);
                        IDnsQueryResponse result = await lookup.QueryAsync(toDomain, QueryType.MX, cancellationToken: cancelToken);
                        message.From.Clear();
                        message.From.Add(from);
                        onPrepare?.Invoke(message);
                        foreach (DnsClient.Protocol.MxRecord record in result.AllRecords)
                        {
                            // attempt to send, if fail, try next address
                            try
                            {
                                MailDemonLog.Debug("GetHostEntryAsync for exchange {0}", record.Exchange);
                                ip = await Dns.GetHostEntryAsync(record.Exchange);
                                foreach (IPAddress ipAddress in ip.AddressList)
                                {
                                    if (DisableSending)
                                    {
                                        return;
                                    }
                                    string host = ip.HostName;
                                    try
                                    {
                                        MailDemonLog.Debug("Sending message to host {0}, from {1}, to {2}", host, message.From, message.To);
                                        await client.ConnectAsync(host, options: MailKit.Security.SecureSocketOptions.StartTlsWhenAvailable, cancellationToken: cancelToken).TimeoutAfter(30000);
                                        foreach (MailboxAddress address in toAddresses)
                                        {
                                            message.To.Clear();
                                            message.To.Add(address);
                                            if (dkimSigner != null)
                                            {
                                                message.Prepare(EncodingConstraint.SevenBit);
                                                message.Sign(dkimSigner, headersToSign);
                                            }
                                            await client.SendAsync(message, cancelToken).TimeoutAfter(30000);
                                        }
                                        return;
                                    }
                                    catch (Exception ex)
                                    {
                                        MailDemonLog.Error(ex);
                                    }
                                    finally
                                    {
                                        try
                                        {
                                            await client.DisconnectAsync(true, cancelToken);
                                        }
                                        catch
                                        {
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                MailDemonLog.Error(ex);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MailDemonLog.Error(ex);
            }
        }
    }
}