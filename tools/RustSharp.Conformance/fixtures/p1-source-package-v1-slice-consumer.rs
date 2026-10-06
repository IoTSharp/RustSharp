use SourceProducer::{view, add};

fn main() {
    let mut values = [41, 7];
    add(&mut values);
    let slice = view(&values);
    if slice.len() == 2 { println!("{}", slice[0]); }
}
