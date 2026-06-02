using System;
using System.IO;
using System.Net;
using System.Net.Mail;
using System.Xml.Linq;

namespace PDMLite
{
    internal static class EmailManager
    {
        private const string ConfigPath = @"N:\PDM-SolidWorks\vault\email.config";

        // ── Load config — returns null if file missing or Enabled=false ──
        private static EmailConfig LoadConfig()
        {
            if (!File.Exists(ConfigPath)) return null;
            try
            {
                var root = XDocument.Load(ConfigPath).Root;
                if (root == null) return null;
                bool enabled = string.Equals((string)root.Element("Enabled"),
                    "true", StringComparison.OrdinalIgnoreCase);
                if (!enabled) return null;
                return new EmailConfig
                {
                    SmtpServer    = (string)root.Element("SmtpServer")    ?? "",
                    SmtpPort      = (int?)root.Element("SmtpPort")         ?? 587,
                    SenderEmail   = (string)root.Element("SenderEmail")   ?? "",
                    SenderPassword= (string)root.Element("SenderPassword") ?? "",
                    EmailDomain   = (string)root.Element("EmailDomain")   ?? "richardswilcox.com"
                };
            }
            catch { return null; }
        }

        private static string ToEmail(string username, string domain) =>
            username.ToLower() + "@" + domain;

        // ── Notify both Masters: a request was submitted ─────────────────
        public static void NotifyRequestSubmitted(string requestType, string fileName,
            string partNo, string rev, string requester, string note)
        {
            var cfg = LoadConfig();
            if (cfg == null) return;

            string subject = $"BCore PDM — {requestType} Request: {fileName}";
            string body =
                $"A {requestType.ToLower()} request has been submitted and is awaiting your approval.\r\n\r\n" +
                $"File      : {fileName}\r\n" +
                (string.IsNullOrEmpty(partNo) ? "" : $"Part No   : {partNo}\r\n") +
                (string.IsNullOrEmpty(rev)    ? "" : $"Revision  : {rev}\r\n") +
                $"Requested : {requester}\r\n" +
                (string.IsNullOrEmpty(note)   ? "" : $"Note      : {note}\r\n") +
                "\r\nOpen SOLIDWORKS and click PENDING REQUESTS in BCore PDM to approve or reject.";

            string[] masters = { "bchougule", "rkramarz" };
            foreach (string m in masters)
                TrySend(cfg, ToEmail(m, cfg.EmailDomain), subject, body);
        }

        // ── Notify the engineer: their request was approved ──────────────
        public static void NotifyRequestApproved(string requestType, string fileName,
            string requestedBy)
        {
            var cfg = LoadConfig();
            if (cfg == null) return;

            string subject = $"BCore PDM — {requestType} Request Approved: {fileName}";
            string body =
                $"Your {requestType.ToLower()} request has been approved.\r\n\r\n" +
                $"File : {fileName}\r\n\r\n" +
                "You may now proceed with the file in SOLIDWORKS.";

            TrySend(cfg, ToEmail(requestedBy, cfg.EmailDomain), subject, body);
        }

        // ── Notify the engineer: their request was rejected ──────────────
        public static void NotifyRequestRejected(string requestType, string fileName,
            string requestedBy, string note = "")
        {
            var cfg = LoadConfig();
            if (cfg == null) return;

            string subject = $"BCore PDM — {requestType} Request Rejected: {fileName}";
            string body =
                $"Your {requestType.ToLower()} request has been rejected.\r\n\r\n" +
                $"File : {fileName}\r\n" +
                (string.IsNullOrEmpty(note) ? "" : $"Note : {note}\r\n") +
                "\r\nContact your Master for more details.";

            TrySend(cfg, ToEmail(requestedBy, cfg.EmailDomain), subject, body);
        }

        // ── Send a diagnostic test email — returns a human-readable result.
        //    Unlike the notification methods, this surfaces the real error so
        //    the user can fix the config. Sends to the sender address itself,
        //    which isolates SMTP/auth from whether recipient mailboxes exist. ──
        public static string SendTestEmail(out bool success)
        {
            success = false;

            if (!File.Exists(ConfigPath))
                return "No config file found at:\n" + ConfigPath +
                       "\n\nRestart SOLIDWORKS to auto-create the template, " +
                       "then fill it in.";

            EmailConfig cfg;
            try
            {
                var root = XDocument.Load(ConfigPath).Root;
                bool enabled = string.Equals((string)root.Element("Enabled"),
                    "true", StringComparison.OrdinalIgnoreCase);
                if (!enabled)
                    return "Email is turned OFF.\n\nSet <Enabled>true</Enabled> in:\n" +
                           ConfigPath;

                cfg = new EmailConfig
                {
                    SmtpServer    = (string)root.Element("SmtpServer")     ?? "",
                    SmtpPort      = (int?)root.Element("SmtpPort")          ?? 587,
                    SenderEmail   = (string)root.Element("SenderEmail")    ?? "",
                    SenderPassword= (string)root.Element("SenderPassword") ?? "",
                    EmailDomain   = (string)root.Element("EmailDomain")    ?? "richardswilcox.com"
                };
            }
            catch (Exception ex)
            {
                return "Could not read email.config:\n\n" + ex.Message;
            }

            if (string.IsNullOrWhiteSpace(cfg.SenderEmail))
                return "SenderEmail is blank in email.config.";
            if (string.IsNullOrWhiteSpace(cfg.SenderPassword))
                return "SenderPassword is blank in email.config.\n\n" +
                       "Contact IT for the Mailgun SMTP password.";

            // Send to the logged-in user's own address — same derivation used
            // for real notifications — so they actually receive it and can
            // confirm end-to-end delivery (the sender address is send-only).
            string to = ToEmail(PDMLiteAddin.CurrentUser, cfg.EmailDomain);

            try
            {
                using (var client = new SmtpClient(cfg.SmtpServer, cfg.SmtpPort))
                {
                    client.EnableSsl = true;
                    client.Credentials = new NetworkCredential(
                        cfg.SenderEmail, cfg.SenderPassword);
                    client.Timeout = 15000;

                    using (var msg = new MailMessage(cfg.SenderEmail, to,
                        "BCore PDM — Test Email",
                        "This is a test email from BCore PDM.\r\n\r\n" +
                        "If you can read this, your email notifications are " +
                        "configured correctly."))
                        client.Send(msg);
                }
            }
            catch (Exception ex)
            {
                string detail = ex.Message;
                if (ex.InnerException != null)
                    detail += "\n\n" + ex.InnerException.Message;
                return "Send FAILED:\n\n" + detail +
                       "\n\nCommon causes:\n" +
                       "• Wrong SMTP password (ask IT for the Mailgun password)\n" +
                       "• SenderEmail doesn't match the Mailgun account\n" +
                       "• Network/firewall blocks SMTP port " + cfg.SmtpPort;
            }

            success = true;
            return "SUCCESS — test email sent to:\n" + to +
                   "\n\nCheck that inbox to confirm it arrived.";
        }

        // ── Internal send — never throws, always fire-and-forget ─────────
        private static void TrySend(EmailConfig cfg, string to,
            string subject, string body)
        {
            try
            {
                using (var client = new SmtpClient(cfg.SmtpServer, cfg.SmtpPort))
                {
                    client.EnableSsl = true;
                    client.Credentials = new NetworkCredential(
                        cfg.SenderEmail, cfg.SenderPassword);
                    client.Timeout = 10000;

                    using (var msg = new MailMessage(cfg.SenderEmail, to, subject, body))
                        client.Send(msg);
                }
            }
            catch { /* non-fatal — email failure never blocks workflow */ }
        }

        // ── Create a template config file for first-time setup ───────────
        public static void EnsureConfigTemplate()
        {
            if (File.Exists(ConfigPath)) return;
            try
            {
                string dir = Path.GetDirectoryName(ConfigPath);
                if (!Directory.Exists(dir)) return;
                File.WriteAllText(ConfigPath,
                    "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
                    "<!-- BCore PDM Email Configuration\r\n" +
                    "     Set Enabled to true and fill in SMTP credentials to activate.\r\n" +
                    "     Contact IT for Mailgun SMTP credentials. -->\r\n" +
                    "<EmailConfig>\r\n" +
                    "  <Enabled>false</Enabled>\r\n" +
                    "  <SmtpServer>smtp.mailgun.org</SmtpServer>\r\n" +
                    "  <SmtpPort>587</SmtpPort>\r\n" +
                    "  <SenderEmail>bcorepdm@mg.richardswilcox.com</SenderEmail>\r\n" +
                    "  <SenderPassword>your-mailgun-smtp-password-here</SenderPassword>\r\n" +
                    "  <EmailDomain>richardswilcox.com</EmailDomain>\r\n" +
                    "</EmailConfig>\r\n");
            }
            catch { }
        }
    }

    internal class EmailConfig
    {
        public string SmtpServer     { get; set; }
        public int    SmtpPort       { get; set; }
        public string SenderEmail    { get; set; }
        public string SenderPassword { get; set; }
        public string EmailDomain    { get; set; }
    }
}
