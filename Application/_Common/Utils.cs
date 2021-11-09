﻿using Application.Common.Interfaces;
using Application.Options;
using Application.VaccineCredential.Queries.GetVaccineStatus;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SendGrid.Helpers.Mail;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace Application.Common
{
    public class Utils
    {
        public static readonly ImmutableDictionary<string, string> VaccineTypeNames = new Dictionary<string, string>
        {
            { "207", "Moderna" },
            { "208", "Pfizer" },
            { "210", "AstraZeneca" },
            { "211", "Novavax" },
            { "212", "J&J" },
            { "213", "COVID-19, unspecified" },
        }.ToImmutableDictionary();

        private static AppSettings _appSettings;

        private static int messageCalls = 0;

        //private static int noMatchCalls = 0;
        public Utils(AppSettings appSettings)
        {
            _appSettings = appSettings;
        }

        public static int ValidatePin(string pin)
        {
            //Business Rules:  1)  4 digit pin
            //    2)  Not 4 or more of the same # eg:  0000,1111 (not allow)
            //    3)  No more than 3 consecutive #, eg: 1234 (not allow)
            if (pin == null)
            {
                return 1;
            }
            else if (pin.Length != 4)
            {
                return 2;
            }
            else if (!Int32.TryParse(pin, out _))
            {
                return 3;
            }
            //    2)  Not 4 or more of the same # eg:  1111 (not allow)
            else if (ContainsDupsChars(pin, 4))
            {
                return 4;
            }
            //   3)  No consecutive #, eg: 1234 (not allow)
            else if (HasConsecutive(pin, 4))
            {
                return 5;
            }
            else
            {
                return 0;
            }
        }

        public static bool ContainsDupsChars(string s, int max)
        {
            if (string.IsNullOrEmpty(s))
            {
                return false;
            }

            int cnt = 0;
            for (int i = 0; i < s.Length - 1 && cnt < max; ++i)
            {
                var charI = s[i];
                cnt = s.Count(c => c == charI);
                if (cnt >= max)
                {
                    return true;
                }
            }

            return false;
        }

        public static bool HasConsecutive(string s, int max)
        {
            int cnt = 0;
            for (int i = 0; i < s.Length - 1; i++)
            {
                var chr1 = s[i];
                var chr2 = s[i + 1];
                if (chr1 + 1 == chr2)
                {
                    cnt++;
                }
                else
                {
                    cnt = 0;
                }
                if (cnt >= max - 1)
                {
                    break;
                }
            }

            return cnt >= max - 1;
        }

        public static string ParseLotNumber(string s)
        {   // Check if lot number is Alpha numeric
            if (s == null) { return null; }

            var regex = "^[a-zA-Z0-9-]*$";
            var regexContainsNumber = "[\\d]";
            var tokens = s.Split(" ");

            foreach (var t in tokens)
            {
                if (Regex.IsMatch(t, regex) && Regex.IsMatch(t, regexContainsNumber))
                {
                    return t;
                }
            }

            return null;
        }

        public static string TrimString(string s, int i)
        {
            if (s == null) { return null; }

            if (s.Length > i)
            {
                s = s.Substring(0, i);
            }
            return s;
        }

        //returns 0 if cached
        //        1 if not in db
        //        2 if email send success
        //        3 if sms send successs
        //        4 if email error
        //        5 is sms error
        public static async Task<int> ProcessStatusRequest(AppSettings _appSettings, ILogger logger, IEmailService _emailService, SendGridSettings _sendGridSettings, IMessagingService _messagingService, IAesEncryptionService _aesEncryptionService, GetVaccineCredentialStatusQuery request, IAzureSynapseService _azureSynapseService, SqlConnection conn, CancellationToken cancellationToken, long tryCount = 1)
        {
            Interlocked.Increment(ref messageCalls);
            int ret = 0;

            var smsRecipient = request.PhoneNumber;
            if (!string.IsNullOrWhiteSpace(_appSettings.DeveloperSms))
            {
                smsRecipient = _appSettings.DeveloperSms;
                logger.LogInformation($"OVERRIDE: Sending to developer SMS instead of request.PhoneNumber; request.Id={request.Id}");
            }
            var emailRecipient = request.EmailAddress;
            if (!string.IsNullOrWhiteSpace(_appSettings.DeveloperEmail))
            {
                emailRecipient = _appSettings.DeveloperEmail;
                logger.LogInformation($"OVERRIDE: Sending to developer email instead of request.EmailAddress; request.Id={request.Id}");
            }

            // Get Vaccine Credential
            string response;
            response = await _azureSynapseService.GetVaccineCredentialStatusAsync(request, cancellationToken);

            var logMessage = $"searchCriteria= {Sanitize(request.FirstName.Substring(0, 1))}.{Sanitize(request.LastName.Substring(0, 1))}. {((DateTime)request.DateOfBirth).ToString("MM/dd/yyyy")} {Sanitize(request.PhoneNumber)}{request.EmailAddress} {Sanitize(request.Pin)} response={Sanitize(response)}";

            if (!string.IsNullOrEmpty(response))
            {
                //Generate link url with the GUID and send text or email based on the request preference.
                //Encyrpt the response with  aesencrypt
                var code = DateTime.Now.Ticks + "~" + request.Pin + "~" + response;
                var encrypted = _aesEncryptionService.EncryptGcm(code, _appSettings.CodeSecret);
                logger.LogInformation($"ENCRYPTION: encrypted={encrypted}; request.Id={request.Id}");

                var url = $"{_appSettings.WebUrl}/qr/{request.Language}/{encrypted}";

                //Twilio for SMS.
                if (!string.IsNullOrEmpty(request.PhoneNumber))
                {
                    ret = 3;
                    var messageId = await _messagingService.SendMessageAsync(smsRecipient, FormatSms2(url, request.Language), request.Id, cancellationToken);
                    messageId = await _messagingService.SendMessageAsync(smsRecipient, FormatSms(Convert.ToInt32(_appSettings.LinkExpireHours), request.Language), request.Id, cancellationToken);

                    if (string.IsNullOrEmpty(messageId))
                    {
                        ret = 5;
                    }
                    else if (messageId == "FAILED")
                    {
                        ret = 6;
                    }
                }

                //SendGrid for email
                if (!string.IsNullOrEmpty(request.EmailAddress))
                {
                    ret = 2;
                    var message = new SendGridMessage();
                    message.AddTo(emailRecipient, $"{UppercaseFirst(request.FirstName)} {UppercaseFirst(request.LastName)}");
                    message.SetFrom(_sendGridSettings.SenderEmail, _sendGridSettings.Sender);
                    message.SetSubject("Digital COVID-19 Verification Record");
                    message.PlainTextContent = FormatSms2(url, request.Language) + "/n" + FormatSms(Convert.ToInt32(_appSettings.LinkExpireHours), request.Language); ;
                    message.HtmlContent = FormatHtml(url, request.Language, Convert.ToInt32(_appSettings.LinkExpireHours), _appSettings.WebUrl, _appSettings.CDCUrl, _appSettings.VaccineFAQUrl, _appSettings.CovidWebUrl, _appSettings.EmailLogoUrl);

                    if (!(await _emailService.SendEmailAsync(message, emailRecipient, request.Id)))
                    {
                        ret = 4;
                    }
                }
            }
            else
            {
                ret = 1;

                //Email sms request that we could not find you
                if (!string.IsNullOrWhiteSpace(request.PhoneNumber))
                {
                    if (_appSettings.SendNotFoundSms != "0")
                    {
                        await _messagingService.SendMessageAsync(smsRecipient, FormatNotFoundSms(request.Language, _appSettings.SupportPhoneNumber), request.Id, cancellationToken);
                    }
                }
                else
                {
                    if (_appSettings.SendNotFoundEmail != "0")
                    {
                        var message = new SendGridMessage();
                        message.AddTo(emailRecipient, $"{UppercaseFirst(request.FirstName)} {UppercaseFirst(request.LastName)}");
                        message.SetFrom(_sendGridSettings.SenderEmail, _sendGridSettings.Sender);
                        message.SetSubject("Digital COVID-19 Vaccine Record");
                        message.PlainTextContent = FormatNotFoundSms(request.Language, _appSettings.SupportPhoneNumber);
                        message.HtmlContent = FormatNotFoundHtml(request.Language, _appSettings.WebUrl, _appSettings.ContactUsUrl, _appSettings.VaccineFAQUrl, _appSettings.CovidWebUrl, _appSettings.EmailLogoUrl);
                        await _emailService.SendEmailAsync(message, emailRecipient, request.Id);
                    }
                }
            }

            if (tryCount <= 1)
            {
                switch (ret)
                {
                    case 0:
                        logger.LogInformation($"CACHEDREQUEST: {logMessage}; request.Id={request.Id}");
                        break;

                    case 1:
                        logger.LogInformation($"BADREQUEST-NOTFOUND: {logMessage}; request.Id={request.Id}");
                        break;

                    case 2:
                        logger.LogInformation($"VALIDREQUEST-EMAILSENT: {logMessage}; request.Id={request.Id}");
                        break;

                    case 3:
                        logger.LogInformation($"VALIDREQUEST-SMSSENT: {logMessage}; request.Id={request.Id}");
                        break;

                    case 4:
                        logger.LogWarning($"VALIDREQUEST-EMAILFAILED: {logMessage}; request.Id={request.Id}");
                        break;
                    //case 5:
                    case 6:
                        logger.LogWarning($"VALIDREQUEST-SMSFAILED: {logMessage}; request.Id={request.Id}");
                        break;
                    default:
                        break;
                }
            }
            else
            {
                logger.LogInformation($"RETRY: cnt={tryCount}; ret={ret}; {logMessage}; request.Id={request.Id}");
            }

            return ret;
        }

        /*
         es: Spanish
         cn: Chinese Simplified
         tw: Chinese Traditional
         kr: Korean
         vi: Vietnamese
         ae: Arabic
         ph: Tagalog
         */
        public static string FormatSms(int linkExpireHours, string lang)
        {
            return lang switch
            {
                "es" => $"Gracias por visitar el portal del Registro digital de vacunación contra el COVID-19 del estado de Washington. El enlace para recuperar su código de registro de vacunación contra el COVID-19 tiene una validez de {linkExpireHours} horas. Una vez accedido y guardado en su dispositivo, el código QR no expirará.",
                "cn" => $"感谢访问加利福尼亚州数字新冠肺炎疫苗接种记录门户网站。您的新冠肺炎疫苗接种记录的链接在 {linkExpireHours} 小时内有效。访问并保存到设备后，二维码将不会过期",
                "tw" => $"感謝您造訪加州的數位 COVID-19 疫苗接種記錄入口網站。用來擷取新冠肺炎疫苗接種記錄代碼之連結的有效期間為 {linkExpireHours} 小時。存取並儲存到裝置後，二維碼就不會過期",
                "kr" => $"캘리포니아주의 디지털 코로나19 백신 기록 포털에 방문해 주셔서 감사합니다. 코로나19 백신 기록 코드를 되찾을 수 있는 링크는 {linkExpireHours}시간 동안 유효합니다. 액세스하여 기기에 저장한 QR 코드는 만료되지 않습니다.",
                "vi" => $"Cảm ơn quý vị đã truy cập vào cổng thông tin Hồ sơ Vắc xin COVID-19 Kỹ thuật số của Tiểu bang Washington. Đường liên kết để truy xuất mã hồ sơ vắc xin COVID-19 của quý vị chỉ có giá trị trong {linkExpireHours} giờ. Sau khi truy cập và lưu vào thiết bị, mã QR của quý vị sẽ không hết hạn.",
                "ae" => $"نشكرك على زيارة البوابة الإلكترونية للسجل الرقمي للقاح فيروس كورونا (كوفيد-19) بولاية كاليفورنيا. يُعد الرابط الخاص باسترداد رمز سجل لقاح فيروس كورونا (كوفيد-19) الخاص بك ساريًا لمدة {linkExpireHours} ساعة. بمجرد الوصول إليه وحفظه على جهازك، لن تنتهي صلاحية رمز الاستجابة السريعة.",
                "ph" => $"Salamat sa pagbisita sa portal ng Digital na Rekord ng Bakuna para sa COVID-19 ng Estado ng Washington.  {linkExpireHours} na oras lang may bisa ang link para kunin ang iyong code ng bakuna para sa COVID-19. Kapag na-access na at na-save sa iyong device, hindi mag-e-expire ang QR code.",
                _ => $"Thank you for visiting the Digital COVID-19 Verification Record system. The link to retrieve your COVID-19 verification is valid for {linkExpireHours} hours. Once accessed and saved to your device, the QR code will not expire.",
            };
        }

        public static string FormatSms2(string url, string lang)
        {
            return lang switch
            {
                "es" => $"{url}",
                "cn" => $"{url}",
                "tw" => $"{url}",
                "kr" => $"{url}",
                "vi" => $"{url}",
                "ae" => $"{url}",
                "ph" => $"{url}",
                _ => $"{url}",
            };
        }

        public static string FormatHtml(string url, string lang, int linkExpireHours, string webUrl, string cdcUrl, string vaccineFAQUrl, string covidWebUrl, string emailLogoUrl)
        {
            if (String.IsNullOrEmpty(url))
                throw new Exception("url is null");

            if (String.IsNullOrEmpty(lang))
                throw new Exception("lang is null");

            if (String.IsNullOrEmpty(webUrl))
                throw new Exception("webUrl is null");

            if (String.IsNullOrEmpty(cdcUrl))
                throw new Exception("cdcUrl is null");

            if (String.IsNullOrEmpty(vaccineFAQUrl))
                throw new Exception("vaccineFAQUrl is null");

            if (String.IsNullOrEmpty(covidWebUrl))
                throw new Exception("covidWebUrl is null");

            if (String.IsNullOrEmpty(emailLogoUrl))
                throw new Exception("emailLogoUrl is null");

            return lang switch
            {
                "es" => $"<img src='{webUrl}/imgs/MyTurn-logo.png'><br/>" +
                            $"<h3 style='color: #f06724'>Registro digital de vacunación contra el COVID-19</h3>" +
                            $"<p>Gracias por visitar el portal del Registro digital de vacunación contra el COVID-19 del estado de Washington. El enlace para recuperar su código de registro de vacunación contra el COVID-19 tiene una validez de {linkExpireHours} horas. Una vez accedido y guardado en su dispositivo, el código QR no expirará.</p>" +
                            $"<p><a href='{url}'>Ver el registro de vacunación </a></p>" +
                            $"<p>Obtenga más información sobre cómo <a href='{cdcUrl}'>protegerse y proteger a los demás</a> de parte de los Centros para el Control y la Prevención de Enfermedades. </p>" +
                            $"<p><b>¿Tiene preguntas?</b></p>" +
                            $"<p><a href='{vaccineFAQUrl}'>Visite nuestra página de preguntas frecuentes</a> para obtener más información sobre su registro digital de vacunación contra el COVID-19.</p>" +
                            $"<p><b>Manténgase informado.</b></p>" +
                            $"<p><a href='{covidWebUrl}'>Consulte la información más reciente</a> sobre el COVID-19 en Washington.</p><br/>" +
                            $"<hr>" +
                            $"<footer><p style='text-align:center'>Correo electrónico oficial del Departamento de Estado de Washington</p>" +
                            $"<p style='text-align:center'><img src='{emailLogoUrl}'></p></footer>",
                "cn" => $"<img src='{webUrl}/imgs/MyTurn-logo.png'><br/>" +
                            $"<h3 style='color: #f06724'>数字新冠肺炎疫苗接种记录</h3>" +
                            $"<p>感谢访问加利福尼亚州数字新冠肺炎疫苗接种记录门户网站。您的新冠肺炎疫苗接种记录的链接在 {linkExpireHours} 小时内有效。访问并保存到设备后，二维码将不会过期。</p>" +
                            $"<p><a href='{url}'>查看疫苗接种记录</a></p>" +
                            $"<p>从疾病控制和预防中心了解有关如何<a href='{cdcUrl}'>保护自己和他人</a>的更多信息。</p>" +
                            $"<p><b>有问题吗？</b></p>" +
                            $"<p><a href='{vaccineFAQUrl}'>访问我们的常见问题页面</a> 以了解有关您的数字新冠肺炎疫苗接种记录的更多信息。</p>" +
                            $"<p><b>保持关注。</b></p>" +
                            $"<p><a href='{covidWebUrl}'>查看有关加利福尼亚州新冠肺炎疫情的</a>最新信息。</p><br/>" +
                            $"<hr>" +
                            $"<footer><p style='text-align:center'>加州事务部官方电子邮件</p>" +
                            $"<p style='text-align:center'><img src='{emailLogoUrl}'></p></footer>",
                "tw" => $"<img src='{webUrl}/imgs/MyTurn-logo.png'><br/>" +
                            $"<h3 style='color: #f06724'>數位 COVID-19 疫苗接種記錄</h3>" +
                            $"<p>感謝您造訪加州的數位新冠肺炎疫苗接種記錄入口網站。用來擷取新冠肺炎疫苗接種記錄代碼之連結的有效期間為 {linkExpireHours} 小時。存取並儲存到裝置後，二維碼就不會過期。</p>" +
                            $"<p><a href='{url}'>檢視疫苗接種記錄</a></p>" +
                            $"<p>透過疾病管制與預防中心進一步瞭解如何<a href='{cdcUrl}'>保護自己和他人。</a></p>" +
                            $"<p><b>有問題嗎？</b></p>" +
                            $"<p><a href='{vaccineFAQUrl}'>造訪常見問題頁面</a>，進一步瞭解您的 COVID-19 疫苗接種數位記錄。</p>" +
                            $"<p><b>隨時注意最新資訊。</b></p>" +
                            $"<p><a href='{covidWebUrl}'>檢視加州關於</a> COVID-19 的最新資訊。</p><br/>" +
                            $"<hr>" +
                            $"<footer><p style='text-align:center'>官方加州部門電子郵件</p>" +
                            $"<p style='text-align:center'><img src='{emailLogoUrl}'></p></footer>",
                "kr" => $"<img src='{webUrl}/imgs/MyTurn-logo.png'><br/>" +
                            $"<h3 style='color: #f06724'>디지털 코로나19 백신 기록</h3>" +
                            $"<p>캘리포니아주의 디지털 코로나19 백신 기록 포털에 방문해 주셔서 감사합니다.코로나19 백신 기록 코드를 되찾을 수 있는 링크는 {linkExpireHours}시간 동안 유효합니다.액세스하여 기기에 저장한 QR 코드는 만료되지 않습니다.</p>" +
                            $"<p><a href='{url}'>백신 기록 보기</a></p>" +
                            $"<p><a href='{cdcUrl}'>스스로와 다른 사람을 보호하는</a> 방법에 대해 질병통제예방센터(CDC)에서 자세히 알아보십시오.</p>" +
                            $"<p><b>질문이 있습니까?</b></p>" +
                            $"<p><a href='{vaccineFAQUrl}'>FAQ 페이지에서</a> 디지털 COVID-19 백신 접종 기록에 대해 더 알아보십시오.</p>" +
                            $"<p><b>최신 정보를 얻으십시오.</b></p>" +
                            $"<p><a href='{covidWebUrl}'>COVID-19 in Washington에서</a>최신 정보를 보십시오.</p><br/>" +
                            $"<hr>" +
                            $"<footer><p style='text-align:center'>공식 캘리포니아주 행정청 이메일</p>" +
                            $"<p style='text-align:center'><img src='{emailLogoUrl}'></p></footer>",
                "vi" => $"<img src='{webUrl}/imgs/MyTurn-logo.png'><br/>" +
                            $"<h3 style='color: #f06724'>Hồ sơ Vắc xin COVID-19 Kỹ thuật số</h3>" +
                            $"<p>Cảm ơn quý vị đã truy cập vào cổng thông tin Hồ sơ Vắc xin COVID-19 Kỹ thuật số của Tiểu bang Washington. Đường liên kết để truy xuất mã hồ sơ vắc xin COVID-19 của quý vị chỉ có giá trị trong {linkExpireHours} giờ. Sau khi truy cập và lưu vào thiết bị, mã QR của quý vị sẽ không hết hạn.</p>" +
                            $"<p><a href='{url}'>Xem Hồ sơ Vắc xin</a></p>" +
                            $"<p>Tìm hiểu thêm về cách <a href='{cdcUrl}'>bảo vệ bản thân và người khác</a> từ Trung tâm Kiểm soát và Phòng ngừa Dịch bệnh. </p>" +
                            $"<p><b>Nếu quý vị có thắc mắc?</b></p>" +
                            $"<p><a href='{vaccineFAQUrl}'>Truy cập trang Các Câu hỏi Thường Gặp</a> để tìm hiểu thêm về Hồ sơ Vắc xin COVID-19 Kỹ thuật số của quý vị.</p>" +
                            $"<p><b>Hãy luôn Cập nhật.</b></p>" +
                            $"<p><a href='{covidWebUrl}'>Xem thông tin mới nhất</a> về COVID-19 ở Washington.</p><br/>" +
                            $"<hr>" +
                            $"<footer><p style='text-align:center'>Email chính thức của Tiểu bang Washington</p>" +
                            $"<p style='text-align:center'><img src='{emailLogoUrl}'></p></footer>",
                "ae" => $"<img dir='rtl' src='{webUrl}/imgs/MyTurn-logo.png'><br/>" +
                            $"<h3 dir='rtl' style='color: #f06724'>السجل الرقمي للقاح فيروس كورونا (كوفيد-19)</h3>" +
                            $"<p dir='rtl'>نشكرك على زيارة البوابة الإلكترونية للسجل الرقمي للقاح فيروس كورونا (كوفيد-19) بولاية كاليفورنيا. يُعد الرابط الخاص باسترداد رمز سجل لقاح فيروس كورونا (كوفيد-19) الخاص بك ساريًا لمدة {linkExpireHours} ساعة. بمجرد الوصول إليه وحفظه على جهازك، لن تنتهي صلاحية رمز الاستجابة السريعة.</p>" +
                            $"<p dir='rtl'><a href='{url}'>عرض سجل اللقاح</a></p>" +
                            $"<p dir='rtl'>تعرَّف على المزيد حول <a href='{cdcUrl}'></a> من مراكز مكافحة الأمراض والوقاية منها. </p>" +
                            $"<p dir='rtl'><b>هل لديك أي أسئلة؟</b></p>" +
                            $"<p dir='rtl'><a href='{vaccineFAQUrl}'>تفضل بزيارة صفحة الأسئلة الشائعة لدينا</a > لمعرفة المزيد حول سجل اللقاح الرقمي لكوفيد-19 الخاص بك.</p>" +
                            $"<p dir='rtl'><b>ابقَ على اطلاع.</b></p>" +
                            $"<p dir='rtl'><a href='{covidWebUrl}'>اطلع على أحدث معلومات</a> كوفيد-19 في كاليفورنيا.</p>" +
                            $"<hr>" +
                            $"<footer><p dir='rtl' style='text-align:center'>البريد الإلكتروني الرسمي لوزارة الخارجية الخاص بولاية كاليفورنيا</p>" +
                            $"<p style='text-align:center'><img src='{emailLogoUrl}'></p></footer>",
                "ph" => $"<img src='{webUrl}/imgs/MyTurn-logo.png'><br/>" +
                            $"<h3 style='color: #f06724'>Digital na Rekord ng Bakuna para sa COVID-19</h3>" +
                            $"<p>Salamat sa pagbisita sa portal ng Digital na Rekord ng Bakuna para sa COVID-19 ng Estado ng Washington. {linkExpireHours} na oras lang may bisa ang link para kunin ang iyong code ng bakuna para sa COVID-19. Kapag na-access na at na-save sa iyong device, hindi mag-e-expire ang QR code.</p>" +
                            $"<p><a href='{url}'>Tingnan ang Rekord ng Bakuna</a></p>" +
                            $"<p>Matuto pa mula sa Centers for Disease Control and Prevention kung paano <a href='{cdcUrl}'>mapoprotektahan ang iyong sarili at ang ibang tao.</a></p>" +
                            $"<p><b>May mga tanong?</b></p>" +
                            $"<p><a href='{vaccineFAQUrl}'>Bisitahin ang aming page ng FAQ</a> para alamin pa ang tungkol sa iyong Digital na Record ng Bakuna sa COVID-19.</p>" +
                            $"<p><b>Makibalita.</b></p>" +
                            $"<p><a href='{covidWebUrl}'>Tingnan ang pinakabagong impormasyon</a> sa COVID-19 sa Washington.</p><br/>" +
                            $"<hr>" +
                            $"<footer><p style='text-align:center'>Opisyal na Email ng Departamento ng Estado ng Washington</p>" +
                            $"<p style='text-align:center'><img src='{emailLogoUrl}'></p></footer>",
                _ => $"<img src='{webUrl}/imgs/MyTurn-logo.png'><br/>" +
                            $"<h3 style='color: #f06724'>Digital COVID-19 Verification Record</h3>" +
                            $"<p>Thank you for visiting the Digital COVID-19 Verification Record system. The link to retrieve your COVID-19 vaccine record code is valid for {linkExpireHours} hours. Once accessed and saved to your device, the QR code will not expire.</p>" +
                            $"<p><a href='{url}'>View Vaccine Record</a></p>" +
                            $"<p>Learn more about how to <a href='{cdcUrl}'>protect yourself and others</a> from the Centers for Disease Control and Prevention.</p>" +
                            $"<p><b>Have questions?</b></p>" +
                            $"<p>Visit our <a href='{vaccineFAQUrl}'>(FAQ)</a> page to learn more about your Digital COVID-19 Verification Record.</p>" +
                            $"<p><b>Stay Informed.</b></p>" +
                            $"<p><a href='{covidWebUrl}'>View the latest information</a> on COVID-19.</p><br/>" +
                            $"<hr>" +
                            $"<footer><p style='text-align:center'>Official Washington State Department of Health e-mail</p>" +
                            $"<p style='text-align:center'><img src='{emailLogoUrl}'></p></footer>"
            };
        }

        public static string FormatNotFoundSms(string lang, string phoneNumber)
        {
            return lang switch
            {
                "es" => $"Recientemente solicitó un registro de vacunación de COVID-19 digital desde MyVaccineRecord.CDPH.ca.gov. Desafortunadamente, la información que dio no coincide con la información que tenemos en nuestro sistema. \nComunicarse con el servicio de ayuda de COVID-19 del CDPH: \n{phoneNumber}",
                "cn" => $"您最近向 MyVaccineRecord.CDPH.ca.gov 索取了数字新冠肺炎疫苗接种记录。很遗憾，您提供的信息与我们系统中的信息不匹配。\n联系 CDPH COVID-19 服务台: \n{phoneNumber}",
                "tw" => $"您最近向 MyVaccineRecord.CDPH.ca.gov 申請了數位 COVID-19 疫苗接種記錄。很遺憾，您提供的資訊與我們系統中的資訊不匹配。\n聯絡 CDPH COVID-19 服務台 :\n{phoneNumber}",
                "kr" => $"최근에 MyVaccineRecord.CDPH.ca.gov에서 COVID-19 백신 기록을 요청하셨습니다.안타깝게도 귀하가 제공하신 정보는 당사 시스템의 정 보와 일치하지 않습니다. \nCDPH COVID-19 헬프 데스크에 문의하십시오. \n{phoneNumber}",
                "vi" => $"Quý vị đang yêu cầu hồ sơ vắc xin COVID-19 kỹ thuật số từ MyVaccineRecord.CDPH.ca.gov. Rất tiếc, thông tin mà quý vị cung cấp không có trên hệ thống của chúng tôi. \nLiên hệ với Bộ phận Trợ giúp CDPH COVID-19: \n{phoneNumber}",
                "ae" => $"طلبت مؤخرا سجل لقاح كوفيد-19 الرقمي من MyVaccineRecord.CDPH.ca.gov. لسوء الحظ،‏ لا تطابق المعلومات التي قدمتها  أي معلومات موجودة في نظامنا. \nاتصل بمكتب المساعدة CDPH COVID-19: \n{phoneNumber}",
                "ph" => $"Humiling ka kamakailan ng digital na rekord ng bakuna para sa COVID-19 mula sa MyVaccineRecord.CDPH.ca.gov. Sa kasamaang-palad, hindi tumutugma sa impormasyon sa aming system ang impormasyong ibinigay mo. \nMakipag-ugnay sa CDPH COVID-19 Help Desk: \n{_appSettings.SupportPhoneNumber}",
                _ => $"You recently requested a digital COVID-19 verification record from the state. Unfortunately, the information you provided does not match information in our system.\nContact us at {phoneNumber}, press # for help in matching your record to your contact information."
            };
        }

        public static string FormatNotFoundHtml(string lang, string webUrl, string contactUsUrl, string vaccineFAQUrl, string covidWebUrl, string emailLogoUrl)
        {
            if (String.IsNullOrEmpty(lang))
                throw new Exception("lang is null");

            if (String.IsNullOrEmpty(webUrl))
                throw new Exception("webUrl is null");

            if (String.IsNullOrEmpty(contactUsUrl))
                throw new Exception("contactUsUrl is null");

            if (String.IsNullOrEmpty(vaccineFAQUrl))
                throw new Exception("vaccineFAQUrl is null");

            if (String.IsNullOrEmpty(covidWebUrl))
                throw new Exception("covidWebUrl is null");

            if (String.IsNullOrEmpty(emailLogoUrl))
                throw new Exception("emailLogoUrl is null");

            return lang switch
            {
                "es" => $"<img src='{webUrl}/imgs/MyTurn-logo.png'><br/>" +
                            $"<h3 style='color: #f06724'>Registro digital de vacunación contra el COVID-19</h3>" +
                            $"<p>Hace poco solicitó un registro digital de vacunación contra el COVID-19 a <a href='{webUrl}'>MyVaccineRecord.CDPH.ca.gov</a>. Desafortunadamente, la información que proporcionó no coincide con la información que tenemos en el sistema. Puede <a href='{webUrl}'>enviar otra solicitud</a> con otro número de teléfono o dirección de correo electrónico, o puede comunicarse con el <a href='{contactUsUrl}'>asistente virtual para COVID-19 del CDPH</a> para obtener ayuda para hacer que su registro coincida con su información de contacto.</p><br/>" +
                            $"<p><b>¿Tiene preguntas?</b></p>" +
                            $"<p><a href='{vaccineFAQUrl}'>Visite nuestra página de preguntas frecuentes</a> para obtener más información sobre su registro digital de vacunación contra el COVID-19.</p>" +
                            $"<p><b>Manténgase informado.</b></p>" +
                            $"<p><a href='{covidWebUrl}'>Consulte la información más reciente</a> sobre el COVID-19 en Washington.</p><br/>" +
                            $"<hr>" +
                            $"<footer><p style='text-align:center'>Correo electrónico oficial del Departamento de Estado de Washington</p>" +
                            $"<p style='text-align:center'><img src='{emailLogoUrl}'></p></footer>",
                "cn" => $"<img src='{webUrl}/imgs/MyTurn-logo.png'><br/>" +
                            $"<h3 style='color: #f06724'>数字新冠肺炎疫苗接种记录</h3>" +
                            $"<p>您最近从 <a href='{webUrl}'>MyVaccineRecord.CDPH.ca.gov</a> 请求了数字新冠肺炎疫苗接种记录。很遗憾，您提供的信息与系统中的信息不符。您可以使用不同的电话号码或电子邮件地址<a href='{webUrl}'>提交另一个请求</a>，或者您可以联系 <a href='{contactUsUrl}'>CDPH 新冠肺炎虚拟助手</a>以帮助将您的记录与您的联系信息进行匹配。</p><br/>" +
                            $"<p><b>有问题吗？</b></p>" +
                            $"<p><a href='{vaccineFAQUrl}'>访问我们的常见问题页面</a> 以了解有关您的数字新冠肺炎疫苗接种记录的更多信息。</p>" +
                            $"<p><b>保持关注。</b></p>" +
                            $"<p><a href='{covidWebUrl}'>查看有关加利福尼亚州新冠肺炎疫情的</a>最新信息。</p><br/>" +
                            $"<hr>" +
                            $"<footer><p style='text-align:center'>加州事务部官方电子邮件</p>" +
                            $"<p style='text-align:center'><img src='{emailLogoUrl}'></p></footer>",
                "tw" => $"<img src='{webUrl}/imgs/MyTurn-logo.png'><br/>" +
                            $"<h3 style='color: #f06724'>數位 COVID-19 疫苗接種記錄</h3>" +
                            $"<p>您最近從 <a href='{webUrl}'>MyVaccineRecord.CDPH.ca.gov</a> 申請了 COVID-19 疫苗接種數位記錄。很遺憾，您提供的資訊與我們系統的資訊不相符。您可以使用不同的電話號碼或電子郵件地址<a href='{webUrl}'>提交另一次申請</a>，或者可以聯絡 <a href='{contactUsUrl}'>CDPH COVID-19 虛擬助理</a>協助比對您的記錄與聯絡資訊。</p><br/>" +
                            $"<p><b>有問題嗎？</b></p>" +
                            $"<p><a href='{vaccineFAQUrl}'>造訪常見問題頁面</a>，進一步瞭解您的 COVID-19 疫苗接種數位記錄。</p>" +
                            $"<p><b>隨時注意最新資訊。</b></p>" +
                            $"<p><a href='{covidWebUrl}'>檢視加州關於</a> COVID-19 的最新資訊。</p><br/>" +
                            $"<hr>" +
                            $"<footer><p style='text-align:center'>官方加州部門電子郵件</p>" +
                            $"<p style='text-align:center'><img src='{emailLogoUrl}'></p></footer>",
                "kr" => $"<img src='{webUrl}/imgs/MyTurn-logo.png'><br/>" +
                            $"<h3 style='color: #f06724'>디지털 코로나19 백신 기록</h3>" +
                            $"<p>최근에 <a href='{webUrl}'>MyVaccineRecord.CDPH.ca.gov</a>에서 COVID-19 백신 기록을 신청하셨습니다. 안타깝게도 귀하가 제공하신 정보는 당사 시스템의 정보와 일치하지 않습니다. 다른 전화 번호나 이메일 주소로 <a href='{webUrl}'>신청서를 새로 제출</a>하거나 <a href='{contactUsUrl}'>CDPH COVID-19 가상 어시스턴트</a>에 문의하여 귀하의 기록이 연락처 정보와 연결되도록 도움을 받을 수 있습니다.</p><br/>" +
                            $"<p><b>질문이 있습니까?</b></p>" +
                            $"<p><a href='{vaccineFAQUrl}'>FAQ 페이지에서</a> 디지털 COVID-19 백신 접종 기록에 대해 더 알아보십시오.</p>" +
                            $"<p><b>최신 정보를 얻으십시오.</b></p>" +
                            $"<p><a href='{covidWebUrl}'>COVID-19 in Washington에서</a>최신 정보를 보십시오.</p><br/>" +
                            $"<hr>" +
                            $"<footer><p style='text-align:center'>공식 캘리포니아주 행정청 이메일</p>" +
                            $"<p style='text-align:center'><img src='{emailLogoUrl}'></p></footer>",
                "vi" => $"<img src='{webUrl}/imgs/MyTurn-logo.png'><br/>" +
                            $"<h3 style='color: #f06724'>Hồ sơ Vắc xin COVID-19 Kỹ thuật số</h3>" +
                            $"<p>Quý vị đang yêu cầu hồ sơ vắc xin COVID-19 kỹ thuật số từ <a href='{webUrl}'>MyVaccineRecord.CDPH.ca.gov</a>. Rất tiếc, thông tin mà quý vị cung cấp không có trên hệ thống. Quý vị có thể <a href='{webUrl}'>gửi yêu cầu khác</a> bằng số điện thoại hoặc địa chỉ email khác hoặc có thể liên hệ <a href='{contactUsUrl}'>Trợ lý Ảo CDPH COVID-19</a> để được giúp tìm hồ sơ bằng thông tin mà quý vị cung cấp.</p><br/>" +
                            $"<p><b>Nếu quý vị có thắc mắc?</b></p>" +
                            $"<p><a href='{vaccineFAQUrl}'>Truy cập trang Các Câu hỏi Thường Gặp</a> để tìm hiểu thêm về Hồ sơ Vắc xin COVID-19 Kỹ thuật số của quý vị.</p>" +
                            $"<p><b>Hãy luôn Cập nhật.</b></p>" +
                            $"<p><a href='{covidWebUrl}'>Xem thông tin mới nhất</a> về COVID-19 ở Washington.</p><br/>" +
                            $"<hr>" +
                            $"<footer><p style='text-align:center'>Email chính thức của Tiểu bang Washington</p>" +
                            $"<p style='text-align:center'><img src='{emailLogoUrl}'></p></footer>",
                "ae" => $"<img src='{webUrl}/imgs/MyTurn-logo.png' dir='rtl'><br/>" +
                            $"<h3 dir='rtl' style='color: #f06724'>السجل الرقمي للقاح فيروس كورونا (كوفيد-19)</h3>" +
                            $"<p dir='rtl'>لقد طلبت مؤخرًا سجل لقاح رقميًا لكوفيد-19 من <a href='{webUrl}'>MyVaccineRecord.CDPH.ca.gov</a>. وللأسف، لا تتطابق المعلومات التي قدمتها مع المعلومات الموجودة في نظامنا.</p>" +
                            $"<p dir='rtl'>يمكنك <a href='{webUrl}'>إرسال طلب آخر</a> باستخدام رقم هاتف أو عنوان بريد إلكتروني مختلف، أو يمكنك الاتصال<a href='{contactUsUrl}'> بالمساعد الافتراضي الخاص بكوفيد-19 التابع لإدارة كاليفورنيا للصحة العامة </a>(CDPH)للمساعدة في مطابقة السجل الخاص بك مع معلومات الاتصال الخاصة بك.</p><br/>" +
                            $"<p dir='rtl'><b>هل لديك أي أسئلة؟</b></p>" +
                            $"<p dir='rtl'><a href='{vaccineFAQUrl}'>تفضل بزيارة صفحة الأسئلة الشائعة لدينا</a > لمعرفة المزيد حول سجل اللقاح الرقمي لكوفيد-19 الخاص بك.</p>" +
                            $"<p dir='rtl'><b>ابقَ على اطلاع.</b></p>" +
                            $"<p dir='rtl'><a href='{covidWebUrl}'>اطلع على أحدث معلومات</a> كوفيد-19 في كاليفورنيا.</p>" +
                            $"<hr>" +
                            $"<footer><p dir='rtl' style='text-align:center'>البريد الإلكتروني الرسمي لوزارة الخارجية الخاص بولاية كاليفورنيا</p>" +
                            $"<p style='text-align:center'><img src='{emailLogoUrl}'></p></footer>",
                "ph" => $"<img src='{webUrl}/imgs/MyTurn-logo.png'><br/>" +
                            $"<h3 style='color: #f06724'>Digital na Rekord ng Bakuna para sa COVID-19</h3>" +
                            $"<p>Kamakailan kayong humiling ng digital na record ng bakuna sa COVID-19 mula sa <a href='{webUrl}'>MyVaccineRecord.CDPH.ca.gov</a>. Sa kasawiang-palad, ang impormasyong ibinigay ninyo ay hindi tumutugma sa impormasyon sa aming system. Puwede kayong <a href='{webUrl}'>magsumite ng isa pang kahilingan</a> gamit ang ibang numero ng telepono o email address, o puwede ninyong i-contact ang <a href='{contactUsUrl}'>CDPH COVID-19 Virtual Assistant</a> para sa tulong sa pagtutugma ng inyong record sa inyong contact information.</p><br/>" +
                            $"<p><b>May mga tanong?</b></p>" +
                            $"<p><a href='{vaccineFAQUrl}'>Bisitahin ang aming page ng FAQ</a> para alamin pa ang tungkol sa iyong Digital na Record ng Bakuna sa COVID-19.</p>" +
                            $"<p><b>Makibalita.</b></p>" +
                            $"<p><a href='{covidWebUrl}'>Tingnan ang pinakabagong impormasyon</a> sa COVID-19 sa Washington.</p><br/>" +
                            $"<hr>" +
                            $"<footer><p style='text-align:center'>Opisyal na Email ng Departamento ng Estado ng Washington</p>" +
                            $"<p style='text-align:center'><img src='{emailLogoUrl}'></p></footer>",
                _ => $"<img src='{webUrl}/imgs/MyTurn-logo.png'><br/>" +
                            $"<h3 style='color: #f06724'>Digital COVID-19 Verification Record</h3>" +
                            $"<p>You recently requested a Digital COVID-19 Verification Record from the <a href='{webUrl}'>Digital COVID-19 Verification Record system</a>. Unfortunately, the information you provided does not match information in the state system. " +
                            $"<p>You can submit another request in the <a href='{webUrl}'>Digital COVID-19 Verification Record system</a> with a different mobile phone number or email address, you can <a href='{contactUsUrl}'>contact us</a> for help in matching your record to your contact information, or you can contact your provider to ensure your information has been submitted to the state system.</p>" +
                            $"<p><b>Have questions?</b></p>" +
                            $"<p>Visit our Frequently Asked Questions <a href='{vaccineFAQUrl}'>(FAQ)</a> page to learn more about your Digital COVID-19 Verification Record.</p>" +
                            $"<p><b>Stay Informed.</b></p>" +
                            $"<p><a href='{covidWebUrl}'>View the latest information</a> on COVID-19.</p><br/>" +
                            $"<hr>" +
                            $"<footer><p style='text-align:center'>Official Washington State Department of Health e-mail</p>" +
                            $"<p style='text-align:center'><img src='{emailLogoUrl}'></p></footer>"
            };
        }

        public static string UppercaseFirst(string s)
        {
            // Check for empty string.
            if (string.IsNullOrEmpty(s))
            {
                return string.Empty;
            }
            //consider BILLY BOB
            s = s.ToLower().Trim();
            var tokens = s.Split(" ").ToList();
            var formattedName = "";
            tokens.RemoveAll(b => b.Length == 0);
            foreach (var token in tokens)
            {
                formattedName += char.ToUpper(token[0]) + token[1..] + " ";
            }
            formattedName = formattedName.Trim();
            return formattedName;
        }

        public static bool InPercentRange(int currentMessageCallCount, int percentToVA)
        {
            if (currentMessageCallCount % 100 < percentToVA)
            {
                return true;
            }
            return false;
        }

        public static string Sanitize(string text)
        {
            if (text == null)
            {
                text = "";
            }
            var neutralizedString = HttpUtility.UrlEncode(SecurityElement.Escape(text));
            neutralizedString = neutralizedString.Replace("\n", "  ").Replace("\r", "");
            return neutralizedString;
        }
    }
}