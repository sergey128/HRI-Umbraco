﻿//-----------------------------------------------------------------------
// <copyright file="SAML20Assertion.cs" company="CoverMyMeds">
//  Copyright (c) 2012 CoverMyMeds.  All rights reserved.
//  This code is presented as reference material only.
// </copyright>
//-----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Text;
using System.Web;
using System.Xml;
using System.Xml.Serialization;
using System.IO;
using ComponentSpace.SAML2.Configuration;
using CoverMyMeds.SAML.Library.Schema;
using NLog;
using SBPGPKeys;
using SBX509;
using SBXMLAdESIntf;
using SBXMLCore;
using SBXMLEnc;
using SBXMLSec;
using SBXMLSig;
using SBXMLTransform;
using System.Web.Hosting;

namespace CoverMyMeds.SAML.Library
{
    /// <summary>
    /// Encapsulate functionality for building a SAML Response using the Schema object
    ///     created by xsd.exe from the OASIS spec
    /// </summary>
    /// <remarks>Lots of guidance from this CodeProject implementation
    ///     http://www.codeproject.com/Articles/56640/Performing-a-SAML-Post-with-C#xx0xx
    /// </remarks>
    public class PgpSAML20Assertion
    {
        private static Logger logger = LogManager.GetCurrentClassLogger();
        private static TElXMLDOMDocument FXMLDocument = new TElXMLDOMDocument();
        /// <summary>
        /// Build a signed XML SAML Response string to be inlcuded in an HTML Form
        /// for POSTing to a SAML Service Provider
        /// </summary>
        /// <param name="Issuer">Identity Provider - Used to match the certificate for verifying 
        ///     Response signing</param>
        /// <param name="AssertionExpirationMinutes">Assertion lifetime</param>
        /// <param name="Audience"></param>
        /// <param name="Subject"></param>
        /// <param name="Recipient"></param>
        /// <param name="Attributes">Dictionary of attributes to send through for user SSO</param>
        /// <param name="SigningCert">X509 Certificate used to sign Assertion</param>
        /// <returns></returns>
        public static string CreateSAML20ResponseAsBase64(string Issuer,
            int AssertionExpirationMinutes,
            string Audience,
            string Subject,
            string Recipient,
            Dictionary<string, string> Attributes,
            string partnerSP)
        {
            // Create SAML Response object with a unique ID and correct version
            var response = new ResponseType()
            {
                ID = "_" + Guid.NewGuid(),
                Version = "2.0",
                IssueInstant = System.DateTime.UtcNow,
                Destination = Recipient.Trim(),
                Issuer = new NameIDType() { Value = Issuer.Trim() },
                Status = new StatusType() { StatusCode = new StatusCodeType() { Value = "urn:oasis:names:tc:SAML:2.0:status:Success" } }
            };

            // Put SAML 2.0 Assertion in Response
            response.Items = new AssertionType[] { CreateSAML20Assertion(Issuer, AssertionExpirationMinutes, Audience, Subject, Recipient, Attributes) };

            byte[] binaryResponse = SerializeAndSignSAMLResponse(response, partnerSP);

            return Convert.ToBase64String(binaryResponse);
        }

        public static void GuideSSO(HttpResponseBase httpResponse, string partnerSp, string subject, Dictionary<string, string> samlAttributes)
        {
            SAMLConfiguration.Load();
            var issuer = SAMLConfiguration.Current.IdentityProviderConfiguration.Name;
            var partner = SAMLConfiguration.Current.GetPartnerServiceProvider(partnerSp);

            var saml = CreateSAML20ResponseAsBase64(issuer, 5, partnerSp,
                    subject,
                    partner.AssertionConsumerServiceUrl,
                    samlAttributes,
                    partnerSp);

            var responseContent = String.Format("<html xmlns=\"http://www.w3.org/1999/xhtml\">"
            + "<body onload=\"document.forms.samlform.submit()\">"
            + "<noscript><p><strong>Note:</strong> Since your browser does not support Javascript, you must press the Continue button once to proceed.</p></noscript>"
            + "<form id=\"samlform\" action=\"{0}\" method=\"post\">"
            + "<div>"
            + "<input type=\"hidden\" name=\"SAMLResponse\" value=\"{1}\" />"
            + "<input type=\"hidden\" name=\"clientId\" value=\"900D3C06-C63C-4F49-B3D2-54A91C771A43\" />"
//            + "<input type=\"hidden\" name=\"RelayState\" value=\"\" />"
            + "</div><noscript><div><input type=\"submit\" value=\"Continue\" /></div></noscript>"
            + "</form>"
            + "</body>"
            + "</html>", partner.AssertionConsumerServiceUrl, saml);
            httpResponse.Write(responseContent);

        }

        /// <summary>
        /// Accepts SAML Response, serializes it to XML and signs using the supplied certificate
        /// </summary>
        /// <param name="Response">SAML 2.0 Response</param>
        /// <param name="partnerSP"></param>
        /// <returns>Serialized XML Document with computed signature as byte array.</returns>
        private static byte[] SerializeAndSignSAMLResponse(ResponseType Response, string partnerSP)
        {
            // saml.config contains relative paths not virtual
            var signatureCertificatePath = HostingEnvironment.MapPath("~/" + SAMLConfiguration.Current.IdentityProviderConfiguration.CertificateFile);
            var signaruteCertificatePassword = SAMLConfiguration.Current.IdentityProviderConfiguration.CertificatePassword;
            var encryptionKeyPath = HostingEnvironment.MapPath("~/" + SAMLConfiguration.Current.GetPartnerServiceProvider(partnerSP).CertificateFile);

            using (var stream = new MemoryStream())
            {
                var ns = new XmlSerializerNamespaces();
                // namespace prefixes so that output layout is like in their sample (SAML_Response_Unencoded_IWS_Working.xml)
                ns.Add("saml2p", "urn:oasis:names:tc:SAML:2.0:protocol");
                ns.Add("saml2", "urn:oasis:names:tc:SAML:2.0:assertion"); 

                var responseSerializer = new XmlSerializer(Response.GetType());
                responseSerializer.Serialize(stream, Response, ns);

                stream.Seek(0, SeekOrigin.Begin);
                FXMLDocument.LoadFromStream(stream);
            }

            SBUtils.Unit.SetLicenseKey(File.ReadAllText(HostingEnvironment.MapPath("~/App_Data/eldos-key.txt")));

            // Assertion signature
            var assertionToSign = FXMLDocument.FindNode("saml2:Assertion", true);
            SignElement(signatureCertificatePath, signaruteCertificatePassword, assertionToSign);
            // Assertion encryption
            EncryptAssertion(encryptionKeyPath, assertionToSign);
            // Response signature
            var responseToSign = FXMLDocument.FindNode("saml2p:Response", true);
            SignElement(signatureCertificatePath, signaruteCertificatePassword, responseToSign);

            using (var stream = new MemoryStream())
            {
                FXMLDocument.SaveToStream(stream, SBXMLDefs.Unit.xcmExclCanonComment, new SBXMLCharsets.TElXMLUTF8Codec { WriteBOM = false });
                return stream.ToArray();
            }
        }

        static bool ValidateSignature(TElXMLDOMElement element) // this should be in separate unit test project
        {
            using (var X509KeyData = new TElXMLKeyInfoX509Data(true))
            {
                using (var stream = new FileStream(HostingEnvironment.MapPath("~/App_Data/hrinyorg-prod-public.cer"), FileMode.Open, FileAccess.Read))
                    LoadCertificate(stream, "", X509KeyData);

                using (var verifier = new TElXMLVerifier())
                {
                    verifier.KeyData = X509KeyData;
                    verifier.Load(element);
                    return verifier.ValidateSignature();
                }
            }
        }
        static bool ValidateSignature(string path)
        {
            using (var doc = new TElXMLDOMDocument())
            {
                doc.LoadFromFile(path, "UTF-8");
                return ValidateSignature(doc.DocumentElement);
            }
        }

        /// <summary>
        /// Creates a SAML 2.0 Assertion Segment for a Response
        /// Simple implmenetation assuming a list of string key and value pairs
        /// </summary>
        /// <param name="Issuer"></param>
        /// <param name="AssertionExpirationMinutes"></param>
        /// <param name="Audience"></param>
        /// <param name="Subject"></param>
        /// <param name="Recipient"></param>
        /// <param name="Attributes">Dictionary of string key, string value pairs</param>
        /// <returns>Assertion to sign and include in Response</returns>
        private static AssertionType CreateSAML20Assertion(string Issuer,
            int AssertionExpirationMinutes,
            string Audience,
            string Subject,
            string Recipient,
            Dictionary<string, string> Attributes)
        {
            var NewAssertion = new AssertionType
            {
                Version = "2.0",
                IssueInstant = DateTime.UtcNow,
                ID = "_" + Guid.NewGuid().ToString(),
                Issuer = new NameIDType() {Value = Issuer.Trim()}
            };

            // Create Issuer

            // Create Assertion Subject
            var subject = new SubjectType();
            var subjectNameIdentifier = new NameIDType() { Value = Subject.Trim(), Format = "urn:oasis:names:tc:SAML:1.1:nameid-format:unspecified" };
            var subjectConfirmation = new SubjectConfirmationType() { Method = "urn:oasis:names:tc:SAML:2.0:cm:bearer", SubjectConfirmationData = new SubjectConfirmationDataType() { NotOnOrAfter = DateTime.UtcNow.AddMinutes(AssertionExpirationMinutes), Recipient = Recipient } };
            subject.Items = new object[] { subjectNameIdentifier, subjectConfirmation };
            NewAssertion.Subject = subject;

            // Create Assertion Conditions
            var conditions = new ConditionsType
            {
                NotBefore = DateTime.UtcNow,
                NotBeforeSpecified = true,
                NotOnOrAfter = DateTime.UtcNow.AddMinutes(AssertionExpirationMinutes),
                NotOnOrAfterSpecified = true,
                Items =
                    new ConditionAbstractType[]
                    {new AudienceRestrictionType() {Audience = new string[] {Audience.Trim()}}}
            };
            NewAssertion.Conditions = conditions;

            // Add AuthnStatement and Attributes as Items
            var authStatement = new AuthnStatementType() { AuthnInstant = DateTime.UtcNow, SessionIndex = NewAssertion.ID };
            var context = new AuthnContextType();
            context.ItemsElementName = new[] { ItemsChoiceType5.AuthnContextClassRef };
            context.Items = new object[] { "urn:oasis:names:tc:SAML:2.0:ac:classes:unspecified" };
            authStatement.AuthnContext = context;

            var attributeStatement = new AttributeStatementType();
            attributeStatement.Items = new AttributeType[Attributes.Count];
            int i = 0;
            foreach (KeyValuePair<string, string> attribute in Attributes)
            {
                attributeStatement.Items[i] = new AttributeType()
                {
                    Name = attribute.Key,
                    AttributeValue = new object[] { attribute.Value },
                    NameFormat = "urn:oasis:names:tc:SAML:2.0:attrname-format:basic"
                };
                i++;
            }

            NewAssertion.Items = new StatementAbstractType[] { authStatement, attributeStatement };

            return NewAssertion;
        }

        public static Stream GenerateStreamFromString(string s)
        {
            MemoryStream stream = new MemoryStream();
            StreamWriter writer = new StreamWriter(stream);
            writer.Write(s);
            writer.Flush();
            stream.Position = 0;
            return stream;
        }

        private static void EncryptAssertion(string certificate, TElXMLDOMNode nodeToEnrypt)
        {
            //var nodeToEnrypt = FXMLDocument.FindNode("saml2:Assertion", true);

            TElXMLEncryptor Encryptor;
            TElXMLKeyInfoSymmetricData SymKeyData;
            TElXMLKeyInfoRSAData RSAKeyData;
            TElXMLKeyInfoX509Data X509KeyData;
            TElXMLKeyInfoPGPData PGPKeyData;
            FileStream F;
            TElXMLDOMNode EncNode;

            /* partner side:
             * Encryptor.EncryptKey = true;
                Encryptor.EncryptionMethod = SBXMLSec.Unit.xemAES;
                Encryptor.EncryptedDataType = SBXMLSec.Unit.xedtElement;

                Encryptor.KeyEncryptionType = SBXMLSec.Unit.xetKeyTransport;
                Encryptor.KeyTransportMethod = SBXMLSec.Unit.xktRSAOAEP;

                SymKeyData = new TElXMLKeyInfoSymmetricData(true);
                SymKeyData.Key.Generate(32 * 8);
                SymKeyData.Key.GenerateIV(16 * 8);
                Encryptor.KeyData = SymKeyData;
             */

            Encryptor = new TElXMLEncryptor();
            Encryptor.EncryptKey = true;
            Encryptor.EncryptionMethod = 1;
            Encryptor.KeyName = String.Empty;
            Encryptor.EncryptedDataType = 0;
            Encryptor.KeyEncryptionType = 0;
            Encryptor.KeyTransportMethod = 1;
            Encryptor.KeyWrapMethod = 0;

            SymKeyData = new TElXMLKeyInfoSymmetricData(true);
            // generate random Key & IV
            SymKeyData.Key.Generate(32 * 8);
            SymKeyData.Key.GenerateIV(16 * 8);
                     
            Encryptor.KeyData = SymKeyData;

            // xetKeyTransport
            RSAKeyData = new TElXMLKeyInfoRSAData(true);
            RSAKeyData.RSAKeyMaterial.Passphrase = String.Empty;
            X509KeyData = new TElXMLKeyInfoX509Data(true);
            PGPKeyData = new TElXMLKeyInfoPGPData(true);

            certificate = HostingEnvironment.MapPath("~/App_Data/ussitsps_test_pub.asc");
            F = new FileStream(certificate, FileMode.Open, FileAccess.Read);

            //try
            //{
                //RSAKeyData.RSAKeyMaterial.LoadPublic(F, 0);
            //}
            //catch { }

            //if (!RSAKeyData.RSAKeyMaterial.PublicKey)
            //{
            //    F.Position = 0;
            //    try
            //    {
            //        RSAKeyData.RSAKeyMaterial.LoadSecret(F, 0);
            //    }
            //    catch { }
            //}

            //if (!RSAKeyData.RSAKeyMaterial.PublicKey)
            //{
            //    F.Position = 0;
            //    LoadCertificate(F, String.Empty, X509KeyData);
            //}

                //if (!RSAKeyData.RSAKeyMaterial.PublicKey &&
                //    (X509KeyData.Certificate == null))
            {
                //F.Position = 0;
                PGPKeyData.PublicKey = new TElPGPPublicKey();
                try
                {
                    ((TElPGPPublicKey)PGPKeyData.PublicKey).LoadFromStream(F);
                }
                catch
                {
                    PGPKeyData.PublicKey.Dispose();
                    PGPKeyData.PublicKey = null;
                }

                //if (PGPKeyData.PublicKey == null)
                //{
                //    F.Position = 0;
                //    PGPKeyData.SecretKey = new TElPGPSecretKey();
                //    PGPKeyData.SecretKey.Passphrase = String.Empty;
                //    try
                //    {
                //        ((TElPGPSecretKey)PGPKeyData.SecretKey).LoadFromStream(F);
                //    }
                //    catch
                //    {
                //        PGPKeyData.SecretKey = null;
                //    }
                //}
            }

            F.Close();

            //if (RSAKeyData.RSAKeyMaterial.PublicKey)
            //    Encryptor.KeyEncryptionKeyData = RSAKeyData;
            //else
            //    if (X509KeyData.Certificate != null)
            //        Encryptor.KeyEncryptionKeyData = X509KeyData;

            //    else
            //        if ((PGPKeyData.PublicKey != null) ||
            //        (PGPKeyData.SecretKey != null))
                        Encryptor.KeyEncryptionKeyData = PGPKeyData;

            //Encrypt Node
            Encryptor.Encrypt(nodeToEnrypt);
            // Save document
            EncNode = Encryptor.Save(FXMLDocument);

            //Replacing selected node with encrypted node
            var encryptedAssertion = FXMLDocument.CreateElementNS("urn:oasis:names:tc:SAML:2.0:assertion", "saml2:EncryptedAssertion");

            var nsAttr = FXMLDocument.CreateAttribute("xmlns:saml2");
            nsAttr.Value = "urn:oasis:names:tc:SAML:2.0:assertion";
            encryptedAssertion.Attributes.Add(nsAttr);
            encryptedAssertion.AppendChild(EncNode);
            nodeToEnrypt.ParentNode.ReplaceChild(encryptedAssertion, nodeToEnrypt);
            
            Encryptor.Dispose();
            if (X509KeyData != null)
                X509KeyData.Dispose();
            if (PGPKeyData != null)
                PGPKeyData.Dispose();
        }

        private static void LoadCertificate(FileStream F, string Password, TElXMLKeyInfoX509Data X509KeyData)
        {
            int CertFormat;
            X509KeyData.Certificate = new TElX509Certificate();
            try
            {
                CertFormat = TElX509Certificate.DetectCertFileFormat(F);
                F.Position = 0;

                switch (CertFormat)
                {
                    case SBX509.Unit.cfDER:
                        {
                            X509KeyData.Certificate.LoadFromStream(F, 0);
                            break;
                        }
                    case SBX509.Unit.cfPEM:
                        {
                            X509KeyData.Certificate.LoadFromStreamPEM(F, Password, 0);
                            break;
                        }
                    case SBX509.Unit.cfPFX:
                        {
                            X509KeyData.Certificate.LoadFromStreamPFX(F, Password, 0);
                            break;
                        }
                    default:
                        {
                            X509KeyData.Certificate.Dispose();
                            X509KeyData.Certificate = null;
                            break;
                        }
                }
            }
            catch
            {
                X509KeyData.Certificate.Dispose();
                X509KeyData.Certificate = null;
            }
        }

        private static void SignElement(string absoluteCertFilePath, string password, object element)
        {
            TElXMLSigner Signer;
            TElXAdESSigner XAdESSigner = null;
            TElXMLKeyInfoRSAData RSAKeyData = null;
            TElXMLKeyInfoX509Data X509KeyData = null;
            TElXMLKeyInfoPGPData PGPKeyData = null;
            FileStream F;
            TElXMLDOMNode SigNode;

            TElXMLReferenceList Refs = new TElXMLReferenceList();
            TElXMLReference Ref = new TElXMLReference();
            Ref.DigestMethod = SBXMLSec.Unit.xdmSHA1;
            if ((TElXMLDOMNode)element is TElXMLDOMDocument)
            {
                Ref.URINode = ((TElXMLDOMDocument)element).DocumentElement;
                Ref.URI = "";
            }
            else
                if ((TElXMLDOMNode)element is TElXMLDOMElement)
                {
                    Ref.URINode = (TElXMLDOMNode)element;
                    TElXMLDOMElement El = (TElXMLDOMElement)element;
                    if (El.GetAttribute("ID") != "")
                        Ref.URI = "#" + El.GetAttribute("ID");
                    else
                        if (El.ParentNode is TElXMLDOMDocument)
                            Ref.URI = "";
                        else
                        {
                            El.SetAttribute("ID", "id-" + SBStrUtils.Unit.IntToStr(SBRandom.__Global.SBRndGenerate(uint.MaxValue)));
                            Ref.URI = "#" + El.GetAttribute("Id");
                        }
                }
                else
                {
                    Ref.URINode = (TElXMLDOMNode)element;
                    Ref.URI = ((TElXMLDOMNode)element).LocalName;
                }

            Ref.TransformChain.Add(new TElXMLEnvelopedSignatureTransform());
            Ref.TransformChain.Add(new TElXMLC14NTransform());
            Refs.Add(Ref);

            Signer = new TElXMLSigner(); // https://www.eldos.com/documentation/sbb/documentation/ref_cl_xmlsigner_prp_signaturemethodtype.html
            try
            {
                Signer.SignatureType = SBXMLSec.Unit.xstEnveloped;
                Signer.CanonicalizationMethod = SBXMLDefs.Unit.xcmExclCanonComment;
                Signer.SignatureMethodType = SBXMLSec.Unit.xmtSig;
                Signer.SignatureMethod = SBXMLSec.Unit.xsmRSA_SHA1;
                Signer.MACMethod = SBXMLSec.Unit.xmmHMAC_MD5;
                Signer.References = Refs;
                Signer.KeyName = String.Empty;
                Signer.IncludeKey = false;

                Signer.OnFormatElement += FormatElement;
                Signer.OnFormatText += FormatText;

                if ((Signer.SignatureType == SBXMLSec.Unit.xstEnveloping) && (Ref != null) && (Ref.URI == "") && (Ref.URINode is TElXMLDOMElement))
                {
                    TElXMLDOMElement El = (TElXMLDOMElement)Ref.URINode;
                    El.SetAttribute("ID", "id-" + SBStrUtils.Unit.IntToStr(SBRandom.__Global.SBRndGenerate(uint.MaxValue)));
                    Ref.URI = "#" + El.GetAttribute("Id");
                }

                    
                RSAKeyData = new TElXMLKeyInfoRSAData(true);
                RSAKeyData.RSAKeyMaterial.Passphrase = password;
                X509KeyData = new TElXMLKeyInfoX509Data(true);
                PGPKeyData = new TElXMLKeyInfoPGPData(true);

                F = new FileStream(absoluteCertFilePath, FileMode.Open, FileAccess.Read);

                try
                {
                    // trying to load file as RSA key material
                    RSAKeyData.RSAKeyMaterial.LoadSecret(F, 0);
                }
                catch { }

                if (!RSAKeyData.RSAKeyMaterial.SecretKey)
                {
                    // trying to load file as Certificate
                    F.Position = 0;
                    LoadCertificate(F, password, X509KeyData);
                }

                if (!RSAKeyData.RSAKeyMaterial.PublicKey &&
                    (X509KeyData.Certificate == null))
                {
                    // trying to load file as PGP key
                    F.Position = 0;
                    PGPKeyData.SecretKey = new TElPGPSecretKey();
                    PGPKeyData.SecretKey.Passphrase = password;
                    try
                    {
                        ((TElPGPSecretKey)PGPKeyData.SecretKey).LoadFromStream(F);
                    }
                    catch
                    {
                        PGPKeyData.SecretKey = null;
                    }
                }

                F.Close();

                if (RSAKeyData.RSAKeyMaterial.SecretKey)
                    Signer.KeyData = RSAKeyData;
                else if (X509KeyData.Certificate != null)
                {
                    if (!X509KeyData.Certificate.PrivateKeyExists)
                        {
                        throw new Exception("The selected certificate doesn''t contain a private key");
                    }

                    Signer.KeyData = X509KeyData;
                }
                else if (PGPKeyData.SecretKey != null)
                {
                    Signer.KeyData = PGPKeyData;
                }

                Signer.UpdateReferencesDigest();

                Signer.GenerateSignature();

                SigNode = (TElXMLDOMNode)element;
                if (SigNode is TElXMLDOMDocument)
                    SigNode = ((TElXMLDOMDocument)SigNode).DocumentElement;

                try
                {
                    // If the signature type is enveloping, then the signature is placed into the passed node and the contents of the node are moved to inside of the signature. 
                    // If the signature type is enveloped, the signature is placed as a child of the passed node.
                    Signer.Save(ref SigNode);
                }
                catch (Exception E)
                {
                    throw new Exception(string.Format("Failed to sign data and to save the signature: ({0})", E.Message));
                }
            }
            finally
            {
                Signer.Dispose();
                if (XAdESSigner != null)
                    XAdESSigner.Dispose();
                if (X509KeyData != null)
                    X509KeyData.Dispose();
                if (PGPKeyData != null)
                    PGPKeyData.Dispose();
            }
        }

        private static void FormatElement(object Sender, TElXMLDOMElement Element, int Level, string Path, ref string StartTagWhitespace, ref string EndTagWhitespace)
        {
            StartTagWhitespace = "\n";
            string s = new string('\t', Level - 1);

            StartTagWhitespace = StartTagWhitespace + s;
            if (Element.FirstChild != null)
            {
                bool HasElements = false;
                TElXMLDOMNode Node = Element.FirstChild;
                while (Node != null)
                {
                    if (Node.NodeType == SBXMLCore.Unit.ntElement)
                    {
                        HasElements = true;
                        break;
                    }

                    Node = Node.NextSibling;
                }

                if (HasElements)
                    EndTagWhitespace = "\n" + s;
            }
        }

        private static void FormatText(object Sender, ref string Text, short TextType, int Level, string Path)
        {
            if ((TextType == SBXMLDefs.Unit.ttBase64) && (Text.Length > 64))
            {
                string s = "\n";
                while (Text.Length > 0)
                {
                    if (Text.Length > 64)
                    {
                        s = s + Text.Substring(0, 64) + "\n";
                        Text = Text.Remove(0, 64);
                    }
                    else
                    {
                        s = s + Text + "\n";
                        Text = "";
                    }
                }

                Text = s + new string('\t', Level - 2);
            }
        }
    }
}