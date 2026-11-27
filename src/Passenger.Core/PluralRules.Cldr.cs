// Written by tools/cldr.py from the Unicode CLDR 49 (supplemental/plurals.xml, under the Unicode License v3; see
// THIRD_PARTY_NOTICES.md). Run it again rather than edit this.
namespace Passenger.Core;

public static partial class PluralRules
{
    /// <summary>The category a count takes in a language CLDR gives rules of, by its tag; null for one it gives none.</summary>
    private static string? Category(string language, in PluralOperands o) => language switch
    {
        "bm" or "bo" or "dz" or "hnj" or "id" or "ig" or "ii" or "in" or "ja" or "jbo" or "jv" or "jw" or "kde" or "kea" or "km" or "ko" or "lkt" or "lo" or "ms" or "my" or "nqo" or "osa" or "root" or "sah" or "ses" or "sg" or "su" or "th" or "to" or "tpi" or "wo" or "yo" or "yue" or "zh" => Set0(o),
        "am" or "as" or "bn" or "doi" or "fa" or "gu" or "hi" or "kn" or "kok" or "kok-Latn" or "pcm" or "tg" or "vi" or "zu" => Set1(o),
        "ff" or "hy" or "kab" => Set2(o),
        "ast" or "de" or "en" or "et" or "fi" or "fy" or "ia" or "ie" or "io" or "ji" or "lij" or "nl" or "sc" or "sv" or "sw" or "ur" or "yi" => Set3(o),
        "si" => Set4(o),
        "ak" or "bho" or "csw" or "guw" or "ln" or "mg" or "nso" or "pa" or "ti" or "wa" => Set5(o),
        "tzm" => Set6(o),
        "af" or "an" or "asa" or "az" or "bal" or "bem" or "bez" or "bg" or "brx" or "ce" or "cgg" or "chr" or "ckb" or "dv" or "ee" or "el" or "eo" or "eu" or "fo" or "fur" or "gsw" or "ha" or "haw" or "hu" or "jgo" or "jmc" or "ka" or "kaj" or "kcg" or "kk" or "kkj" or "kl" or "ks" or "ksb" or "ku" or "ky" or "lb" or "lg" or "mas" or "mgo" or "ml" or "mn" or "mr" or "nah" or "nb" or "nd" or "ne" or "nn" or "nnh" or "no" or "nr" or "ny" or "nyn" or "om" or "or" or "os" or "pap" or "ps" or "rm" or "rof" or "rwk" or "saq" or "sd" or "sdh" or "seh" or "sn" or "so" or "sq" or "ss" or "ssy" or "st" or "syr" or "ta" or "te" or "teo" or "tig" or "tk" or "tn" or "tr" or "ts" or "ug" or "uz" or "ve" or "vo" or "vun" or "wae" or "xh" or "xog" => Set7(o),
        "da" => Set8(o),
        "is" => Set9(o),
        "mk" => Set10(o),
        "ceb" or "fil" or "tl" => Set11(o),
        "lv" or "prg" => Set12(o),
        "lag" => Set13(o),
        "blo" or "cv" or "ksh" => Set14(o),
        "he" or "iw" => Set15(o),
        "iu" or "naq" or "sat" or "se" or "sma" or "smi" or "smj" or "smn" or "sms" => Set16(o),
        "shi" => Set17(o),
        "mo" or "ro" => Set18(o),
        "bs" or "hr" or "sh" or "sr" => Set19(o),
        "fr" or "pt" => Set20(o),
        "ca" or "gl" or "it" or "lld" or "pt-PT" or "scn" or "vec" => Set21(o),
        "es" => Set22(o),
        "gd" => Set23(o),
        "sl" => Set24(o),
        "dsb" or "hsb" => Set25(o),
        "cs" or "sk" => Set26(o),
        "pl" => Set27(o),
        "be" => Set28(o),
        "lt" => Set29(o),
        "ru" or "uk" => Set30(o),
        "sgs" => Set31(o),
        "br" => Set32(o),
        "mt" => Set33(o),
        "ga" => Set34(o),
        "gv" => Set35(o),
        "kw" => Set36(o),
        "ar" or "ars" => Set37(o),
        "cy" => Set38(o),
        _ => null,
    };

    private static string Set0(in PluralOperands o)
    {
        return "other";
    }

    private static string Set1(in PluralOperands o)
    {
        if (o.I == 0 || o.N == 1)
        {
            return "one";
        }
        return "other";
    }

    private static string Set2(in PluralOperands o)
    {
        if ((o.I == 0 || o.I == 1))
        {
            return "one";
        }
        return "other";
    }

    private static string Set3(in PluralOperands o)
    {
        if (o.I == 1 && o.V == 0)
        {
            return "one";
        }
        return "other";
    }

    private static string Set4(in PluralOperands o)
    {
        if ((o.N == 0 || o.N == 1) || (o.I == 0 && o.F == 1))
        {
            return "one";
        }
        return "other";
    }

    private static string Set5(in PluralOperands o)
    {
        if (In(o.N, 0, 1))
        {
            return "one";
        }
        return "other";
    }

    private static string Set6(in PluralOperands o)
    {
        if (In(o.N, 0, 1) || In(o.N, 11, 99))
        {
            return "one";
        }
        return "other";
    }

    private static string Set7(in PluralOperands o)
    {
        if (o.N == 1)
        {
            return "one";
        }
        return "other";
    }

    private static string Set8(in PluralOperands o)
    {
        if (o.N == 1 || (!(o.T == 0) && (o.I == 0 || o.I == 1)))
        {
            return "one";
        }
        return "other";
    }

    private static string Set9(in PluralOperands o)
    {
        if ((o.T == 0 && (o.I % 10) == 1 && !((o.I % 100) == 11)) || ((o.T % 10) == 1 && !((o.T % 100) == 11)))
        {
            return "one";
        }
        return "other";
    }

    private static string Set10(in PluralOperands o)
    {
        if ((o.V == 0 && (o.I % 10) == 1 && !((o.I % 100) == 11)) || ((o.F % 10) == 1 && !((o.F % 100) == 11)))
        {
            return "one";
        }
        return "other";
    }

    private static string Set11(in PluralOperands o)
    {
        if ((o.V == 0 && (o.I == 1 || o.I == 2 || o.I == 3)) || (o.V == 0 && !((o.I % 10) == 4 || (o.I % 10) == 6 || (o.I % 10) == 9)) || (!(o.V == 0) && !((o.F % 10) == 4 || (o.F % 10) == 6 || (o.F % 10) == 9)))
        {
            return "one";
        }
        return "other";
    }

    private static string Set12(in PluralOperands o)
    {
        if ((o.N % 10m) == 0 || In((o.N % 100m), 11, 19) || (o.V == 2 && (o.F % 100) is >= 11 and <= 19))
        {
            return "zero";
        }
        if (((o.N % 10m) == 1 && !((o.N % 100m) == 11)) || (o.V == 2 && (o.F % 10) == 1 && !((o.F % 100) == 11)) || (!(o.V == 2) && (o.F % 10) == 1))
        {
            return "one";
        }
        return "other";
    }

    private static string Set13(in PluralOperands o)
    {
        if (o.N == 0)
        {
            return "zero";
        }
        if ((o.I == 0 || o.I == 1) && !(o.N == 0))
        {
            return "one";
        }
        return "other";
    }

    private static string Set14(in PluralOperands o)
    {
        if (o.N == 0)
        {
            return "zero";
        }
        if (o.N == 1)
        {
            return "one";
        }
        return "other";
    }

    private static string Set15(in PluralOperands o)
    {
        if ((o.I == 1 && o.V == 0) || (o.I == 0 && !(o.V == 0)))
        {
            return "one";
        }
        if (o.I == 2 && o.V == 0)
        {
            return "two";
        }
        return "other";
    }

    private static string Set16(in PluralOperands o)
    {
        if (o.N == 1)
        {
            return "one";
        }
        if (o.N == 2)
        {
            return "two";
        }
        return "other";
    }

    private static string Set17(in PluralOperands o)
    {
        if (o.I == 0 || o.N == 1)
        {
            return "one";
        }
        if (In(o.N, 2, 10))
        {
            return "few";
        }
        return "other";
    }

    private static string Set18(in PluralOperands o)
    {
        if (o.I == 1 && o.V == 0)
        {
            return "one";
        }
        if (!(o.V == 0) || o.N == 0 || (!(o.N == 1) && In((o.N % 100m), 1, 19)))
        {
            return "few";
        }
        return "other";
    }

    private static string Set19(in PluralOperands o)
    {
        if ((o.V == 0 && (o.I % 10) == 1 && !((o.I % 100) == 11)) || ((o.F % 10) == 1 && !((o.F % 100) == 11)))
        {
            return "one";
        }
        if ((o.V == 0 && (o.I % 10) is >= 2 and <= 4 && !((o.I % 100) is >= 12 and <= 14)) || ((o.F % 10) is >= 2 and <= 4 && !((o.F % 100) is >= 12 and <= 14)))
        {
            return "few";
        }
        return "other";
    }

    private static string Set20(in PluralOperands o)
    {
        if (o.I is >= 0 and <= 1)
        {
            return "one";
        }
        if ((o.E == 0 && !(o.I == 0) && (o.I % 1000000) == 0 && o.V == 0) || !(o.E is >= 0 and <= 5))
        {
            return "many";
        }
        return "other";
    }

    private static string Set21(in PluralOperands o)
    {
        if (o.I == 1 && o.V == 0)
        {
            return "one";
        }
        if ((o.E == 0 && !(o.I == 0) && (o.I % 1000000) == 0 && o.V == 0) || !(o.E is >= 0 and <= 5))
        {
            return "many";
        }
        return "other";
    }

    private static string Set22(in PluralOperands o)
    {
        if (o.N == 1)
        {
            return "one";
        }
        if ((o.E == 0 && !(o.I == 0) && (o.I % 1000000) == 0 && o.V == 0) || !(o.E is >= 0 and <= 5))
        {
            return "many";
        }
        return "other";
    }

    private static string Set23(in PluralOperands o)
    {
        if ((o.N == 1 || o.N == 11))
        {
            return "one";
        }
        if ((o.N == 2 || o.N == 12))
        {
            return "two";
        }
        if ((In(o.N, 3, 10) || In(o.N, 13, 19)))
        {
            return "few";
        }
        return "other";
    }

    private static string Set24(in PluralOperands o)
    {
        if (o.V == 0 && (o.I % 100) == 1)
        {
            return "one";
        }
        if (o.V == 0 && (o.I % 100) == 2)
        {
            return "two";
        }
        if ((o.V == 0 && (o.I % 100) is >= 3 and <= 4) || !(o.V == 0))
        {
            return "few";
        }
        return "other";
    }

    private static string Set25(in PluralOperands o)
    {
        if ((o.V == 0 && (o.I % 100) == 1) || (o.F % 100) == 1)
        {
            return "one";
        }
        if ((o.V == 0 && (o.I % 100) == 2) || (o.F % 100) == 2)
        {
            return "two";
        }
        if ((o.V == 0 && (o.I % 100) is >= 3 and <= 4) || (o.F % 100) is >= 3 and <= 4)
        {
            return "few";
        }
        return "other";
    }

    private static string Set26(in PluralOperands o)
    {
        if (o.I == 1 && o.V == 0)
        {
            return "one";
        }
        if (o.I is >= 2 and <= 4 && o.V == 0)
        {
            return "few";
        }
        if (!(o.V == 0))
        {
            return "many";
        }
        return "other";
    }

    private static string Set27(in PluralOperands o)
    {
        if (o.I == 1 && o.V == 0)
        {
            return "one";
        }
        if (o.V == 0 && (o.I % 10) is >= 2 and <= 4 && !((o.I % 100) is >= 12 and <= 14))
        {
            return "few";
        }
        if ((o.V == 0 && !(o.I == 1) && (o.I % 10) is >= 0 and <= 1) || (o.V == 0 && (o.I % 10) is >= 5 and <= 9) || (o.V == 0 && (o.I % 100) is >= 12 and <= 14))
        {
            return "many";
        }
        return "other";
    }

    private static string Set28(in PluralOperands o)
    {
        if ((o.N % 10m) == 1 && !((o.N % 100m) == 11))
        {
            return "one";
        }
        if (In((o.N % 10m), 2, 4) && !(In((o.N % 100m), 12, 14)))
        {
            return "few";
        }
        if ((o.N % 10m) == 0 || In((o.N % 10m), 5, 9) || In((o.N % 100m), 11, 14))
        {
            return "many";
        }
        return "other";
    }

    private static string Set29(in PluralOperands o)
    {
        if ((o.N % 10m) == 1 && !(In((o.N % 100m), 11, 19)))
        {
            return "one";
        }
        if (In((o.N % 10m), 2, 9) && !(In((o.N % 100m), 11, 19)))
        {
            return "few";
        }
        if (!(o.F == 0))
        {
            return "many";
        }
        return "other";
    }

    private static string Set30(in PluralOperands o)
    {
        if (o.V == 0 && (o.I % 10) == 1 && !((o.I % 100) == 11))
        {
            return "one";
        }
        if (o.V == 0 && (o.I % 10) is >= 2 and <= 4 && !((o.I % 100) is >= 12 and <= 14))
        {
            return "few";
        }
        if ((o.V == 0 && (o.I % 10) == 0) || (o.V == 0 && (o.I % 10) is >= 5 and <= 9) || (o.V == 0 && (o.I % 100) is >= 11 and <= 14))
        {
            return "many";
        }
        return "other";
    }

    private static string Set31(in PluralOperands o)
    {
        if ((o.N % 10m) == 1 && !((o.N % 100m) == 11))
        {
            return "one";
        }
        if (o.N == 2)
        {
            return "two";
        }
        if (!(o.N == 2) && In((o.N % 10m), 2, 9) && !(In((o.N % 100m), 11, 19)))
        {
            return "few";
        }
        if (!(o.F == 0))
        {
            return "many";
        }
        return "other";
    }

    private static string Set32(in PluralOperands o)
    {
        if ((o.N % 10m) == 1 && !((o.N % 100m) == 11 || (o.N % 100m) == 71 || (o.N % 100m) == 91))
        {
            return "one";
        }
        if ((o.N % 10m) == 2 && !((o.N % 100m) == 12 || (o.N % 100m) == 72 || (o.N % 100m) == 92))
        {
            return "two";
        }
        if ((In((o.N % 10m), 3, 4) || (o.N % 10m) == 9) && !(In((o.N % 100m), 10, 19) || In((o.N % 100m), 70, 79) || In((o.N % 100m), 90, 99)))
        {
            return "few";
        }
        if (!(o.N == 0) && (o.N % 1000000m) == 0)
        {
            return "many";
        }
        return "other";
    }

    private static string Set33(in PluralOperands o)
    {
        if (o.N == 1)
        {
            return "one";
        }
        if (o.N == 2)
        {
            return "two";
        }
        if (o.N == 0 || In((o.N % 100m), 3, 10))
        {
            return "few";
        }
        if (In((o.N % 100m), 11, 19))
        {
            return "many";
        }
        return "other";
    }

    private static string Set34(in PluralOperands o)
    {
        if (o.N == 1)
        {
            return "one";
        }
        if (o.N == 2)
        {
            return "two";
        }
        if (In(o.N, 3, 6))
        {
            return "few";
        }
        if (In(o.N, 7, 10))
        {
            return "many";
        }
        return "other";
    }

    private static string Set35(in PluralOperands o)
    {
        if (o.V == 0 && (o.I % 10) == 1)
        {
            return "one";
        }
        if (o.V == 0 && (o.I % 10) == 2)
        {
            return "two";
        }
        if (o.V == 0 && ((o.I % 100) == 0 || (o.I % 100) == 20 || (o.I % 100) == 40 || (o.I % 100) == 60 || (o.I % 100) == 80))
        {
            return "few";
        }
        if (!(o.V == 0))
        {
            return "many";
        }
        return "other";
    }

    private static string Set36(in PluralOperands o)
    {
        if (o.N == 0)
        {
            return "zero";
        }
        if (o.N == 1)
        {
            return "one";
        }
        if (((o.N % 100m) == 2 || (o.N % 100m) == 22 || (o.N % 100m) == 42 || (o.N % 100m) == 62 || (o.N % 100m) == 82) || ((o.N % 1000m) == 0 && (In((o.N % 100000m), 1000, 20000) || (o.N % 100000m) == 40000 || (o.N % 100000m) == 60000 || (o.N % 100000m) == 80000)) || (!(o.N == 0) && (o.N % 1000000m) == 100000))
        {
            return "two";
        }
        if (((o.N % 100m) == 3 || (o.N % 100m) == 23 || (o.N % 100m) == 43 || (o.N % 100m) == 63 || (o.N % 100m) == 83))
        {
            return "few";
        }
        if (!(o.N == 1) && ((o.N % 100m) == 1 || (o.N % 100m) == 21 || (o.N % 100m) == 41 || (o.N % 100m) == 61 || (o.N % 100m) == 81))
        {
            return "many";
        }
        return "other";
    }

    private static string Set37(in PluralOperands o)
    {
        if (o.N == 0)
        {
            return "zero";
        }
        if (o.N == 1)
        {
            return "one";
        }
        if (o.N == 2)
        {
            return "two";
        }
        if (In((o.N % 100m), 3, 10))
        {
            return "few";
        }
        if (In((o.N % 100m), 11, 99))
        {
            return "many";
        }
        return "other";
    }

    private static string Set38(in PluralOperands o)
    {
        if (o.N == 0)
        {
            return "zero";
        }
        if (o.N == 1)
        {
            return "one";
        }
        if (o.N == 2)
        {
            return "two";
        }
        if (o.N == 3)
        {
            return "few";
        }
        if (o.N == 6)
        {
            return "many";
        }
        return "other";
    }
}
