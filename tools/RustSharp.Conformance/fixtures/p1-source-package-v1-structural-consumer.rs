use SourceProducer::{pair, sum, array, add};
fn main() {
    let tuple = pair();
    println!("{}", tuple.1);
    println!("{}", sum(tuple));
    let values = array();
    println!("{}", add(values));
}
