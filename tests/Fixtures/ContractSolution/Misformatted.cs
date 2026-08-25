namespace ContractFixture;

/// <summary>
///     Cleanup bait: indentation, brace placement, and spacing that every ReSharper cleanup profile
///     rewrites, so a cleanup run over this file has a guaranteed mutation to detect. The suite restores
///     this content before each cleanup run — an already-formatted file would make the check vacuous.
/// </summary>
internal class Misformatted
{
        public int Value {get;set;}

            public int Doubled( )
    {
            return Value*2 ;
        }

  public int Tripled(int input){
            return input   *   3;
   }
}
