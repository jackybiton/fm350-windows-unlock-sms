namespace Fm350Sms;

internal static class ContactFilesTests
{
    internal static void Tests()
    {
        string newHeader = "First Name,Middle Name,Last Name,Phonetic First Name,Phonetic Middle Name,Phonetic Last Name,Name Prefix,Name Suffix,Nickname,File As,Organization Name,Organization Title,Organization Department,Birthday,Notes,Photo,Labels,Phone 1 - Label,Phone 1 - Value";
        string row = "\"ישראל ישראלי \",,,,,,,,,,,,,,,,* myContacts,,050-555-0123";
        var c1 = ContactFiles.ReadGoogleCsv(newHeader + "\r\n" + row + "\r\n");
        SelfTest.Check("csv new layout one contact", c1.Count == 1, c1.Count.ToString());
        SelfTest.Check("csv new layout name trimmed", c1.Count == 1 && c1[0].Name == "ישראל ישראלי", c1.Count == 1 ? c1[0].Name : "");
        SelfTest.Check("csv new layout key", c1.Count == 1 && c1[0].NumberKeys.SequenceEqual(["+972505550123"]));

        string h2 = "First Name,Last Name,Phone 1 - Label,Phone 1 - Value,Phone 2 - Label,Phone 2 - Value";
        var c2 = ContactFiles.ReadGoogleCsv(h2 + "\n\"Dan\",\"Cohen\",Mobile,\"050-111-1111 ::: +972 52-222-2222\",Home,03-555-1234\n");
        SelfTest.Check("csv ::: separator + multiple phone columns",
            c2.Count == 1 && c2[0].Name == "Dan Cohen" && c2[0].NumberKeys.SequenceEqual(["+972501111111", "+972522222222", "+97235551234"]),
            c2.Count == 1 ? string.Join("|", c2[0].NumberKeys) : "none");

        var c3 = ContactFiles.ReadGoogleCsv("Name,Given Name,Family Name,Phone 1 - Value\nFull Name,Giv,Fam,0501234567\n");
        SelfTest.Check("csv old layout", c3.Count == 1 && c3[0].NumberKeys.SequenceEqual(["+972501234567"]) && c3[0].Name.Length > 0,
            c3.Count == 1 ? c3[0].Name : "none");

        var c4 = ContactFiles.ReadGoogleCsv("First Name,Nickname,File As,Organization Name,Phone 1 - Value\n,,Filed,Org,0501234567\n,Nick,,Org,0502222222\n,,,Org,0503333333\nNoNumber,,,,\n");
        SelfTest.Check("csv name fallback order",
            c4.Count == 3 && c4[0].Name == "Filed" && c4[1].Name == "Nick" && c4[2].Name == "Org",
            string.Join("|", c4.Select(c => c.Name)));

        var c5 = ContactFiles.ReadGoogleCsv("First Name,Phone 1 - Value\n\"A, \"\"B\"\"\nC\",0501234567\n");
        SelfTest.Check("csv quoted comma/quote/newline", c5.Count == 1 && c5[0].Name == "A, \"B\"\nC", c5.Count == 1 ? c5[0].Name : "none");

        string vcf = "BEGIN:VCARD\r\nVERSION:3.0\r\nFN:דני\r\nTEL;TYPE=CELL:050-111-1111\r\nTEL;TYPE=HOME:+972 3-555-1234\r\nEND:VCARD\r\n"
                   + "BEGIN:VCARD\r\nVERSION:3.0\r\nN:Levi;Yossi;;;\r\nTEL:0522222222\r\nEND:VCARD\r\n"
                   + "BEGIN:VCARD\r\nVERSION:3.0\r\nFN:No Tel\r\nEND:VCARD\r\n";
        var v = ContactFiles.ReadVCard(vcf);
        SelfTest.Check("vcard FN two TEL", v.Count == 2 && v[0].Name == "דני" && v[0].NumberKeys.SequenceEqual(["+972501111111", "+97235551234"]),
            v.Count > 0 ? v[0].Name + ":" + string.Join("|", v[0].NumberKeys) : "none");
        SelfTest.Check("vcard N fallback", v.Count == 2 && v[1].Name == "Yossi Levi", v.Count == 2 ? v[1].Name : "none");

        var vf = ContactFiles.ReadVCard("BEGIN:VCARD\nFN:Long\n Name\nTEL:050\n 1111111\nEND:VCARD\n");
        SelfTest.Check("vcard line unfolding", vf.Count == 1 && vf[0].Name == "LongName" && vf[0].NumberKeys.SequenceEqual(["+972501111111"]),
            vf.Count == 1 ? vf[0].Name + ":" + string.Join("|", vf[0].NumberKeys) : "none");

        var vq = ContactFiles.ReadVCard("BEGIN:VCARD\nFN;CHARSET=UTF-8;ENCODING=QUOTED-PRINTABLE:=D7=93=D7=A0=D7=99\nTEL:0501111111\nEND:VCARD\n");
        SelfTest.Check("vcard quoted-printable utf-8", vq.Count == 1 && vq[0].Name == "דני", vq.Count == 1 ? vq[0].Name : "none");

        var manual = new ContactRow(1, "שם, עם \"פסיק\"", "manual", ["+972505550123", "+972521234567"]);
        string csv = ContactFiles.WriteGoogleCsv([manual, new ContactRow(2, "Solo", "manual", ["+972501111111"])]);
        var rt = ContactFiles.ReadGoogleCsv(csv);
        SelfTest.Check("csv round trip", rt.Count == 2 && rt[0].Name == manual.DisplayName && rt[0].NumberKeys.SequenceEqual(manual.NumberKeys)
            && rt[1].Name == "Solo" && rt[1].NumberKeys.SequenceEqual(["+972501111111"]), csv);
        SelfTest.Check("csv writes display form + Mobile label", csv.Contains("050-555-0123") && csv.Contains("Mobile"));
    }
}
